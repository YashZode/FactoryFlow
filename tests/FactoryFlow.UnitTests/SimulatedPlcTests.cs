using FactoryFlow.PlcSimulator;
using NUnit.Framework;

namespace FactoryFlow.UnitTests;

[TestFixture]
public sealed class SimulatedPlcTests
{
    [TestCase(0.1)] [TestCase(7.5)] [TestCase(250)] [TestCase(500)]
    public void RecipeCompletesAtItsExactTarget(double target)
    {
        var plc = new SimulatedPlc();
        Assert.That(plc.StartBatch(target), Is.True);
        for (int i = 0; i < 110; i++) plc.Tick();
        Assert.Multiple(() =>
        {
            Assert.That(plc.State, Is.EqualTo(PlcState.Completed));
            Assert.That(plc.ActualWeight, Is.EqualTo(target));
            Assert.That(plc.TargetWeight, Is.EqualTo(target));
        });
    }
    [TestCase(0)] [TestCase(-1)] [TestCase(1000001)] [TestCase(double.NaN)] [TestCase(double.PositiveInfinity)]
    public void InvalidTargetDoesNotChangeThePlc(double target)
    {
        var plc = new SimulatedPlc();
        Assert.That(plc.StartBatch(target), Is.False);
        Assert.That(plc.State, Is.EqualTo(PlcState.Idle));
        Assert.That(plc.TargetWeight, Is.EqualTo(500));
    }
    [TestCase(true, true)] [TestCase(false, false)] [TestCase(true, false)]
    public void UnsafeInterlockPreventsStart(bool emergency, bool guard)
    {
        var plc = new SimulatedPlc { EmergencyStopActive = emergency, GuardClosed = guard };
        Assert.That(plc.StartBatch(250), Is.False);
        Assert.That(plc.ActualWeight, Is.Zero);
    }
    [Test]
    public void NextRecipeStartsAfterCompletionAndResetsWeight()
    {
        var plc = new SimulatedPlc(); plc.StartBatch(5); plc.Tick();
        Assert.That(plc.State, Is.EqualTo(PlcState.Completed));
        Assert.That(plc.StartBatch(250), Is.True);
        Assert.That(plc.ActualWeight, Is.Zero);
        Assert.That(plc.TargetWeight, Is.EqualTo(250));
    }
    [Test]
    public void DuplicateStartDoesNotReplaceRunningRecipe()
    {
        var plc = new SimulatedPlc(); plc.StartBatch(250); plc.Tick();
        Assert.That(plc.StartBatch(500), Is.False);
        Assert.That(plc.TargetWeight, Is.EqualTo(250));
        Assert.That(plc.ActualWeight, Is.EqualTo(5));
    }
    [TestCase(true)] [TestCase(false)]
    public void ActiveSafetyCauseFaultsAndBlocksReset(bool emergency)
    {
        var plc = new SimulatedPlc(); plc.StartBatch(); plc.Tick();
        if (emergency) plc.EmergencyStopActive = true; else plc.GuardClosed = false;
        plc.Tick();
        Assert.That(plc.State, Is.EqualTo(PlcState.Fault));
        Assert.That(plc.ResetFault(), Is.False);
        Assert.That(plc.StartBatch(), Is.False);
        Assert.That(plc.ActualWeight, Is.EqualTo(5));
    }
    [Test]
    public void ClearingCauseRequiresExplicitResetBeforeStarting()
    {
        var plc = new SimulatedPlc(); plc.StartBatch(); plc.GuardClosed = false; plc.Tick();
        plc.GuardClosed = true;
        Assert.That(plc.StartBatch(), Is.False);
        Assert.That(plc.ResetFault(), Is.True);
        Assert.That(plc.State, Is.EqualTo(PlcState.Idle));
        Assert.That(plc.StartBatch(250), Is.True);
    }
    [Test]
    public void StopRetainsPartialWeight()
    {
        var plc = new SimulatedPlc(); plc.StartBatch(); plc.Tick(); plc.StopBatch();
        Assert.That(plc.State, Is.EqualTo(PlcState.Idle));
        Assert.That(plc.ActualWeight, Is.EqualTo(5));
    }
    [Test]
    public void StopDoesNotPretendToResetCompletedOrFaultStates()
    {
        var completed = new SimulatedPlc(); completed.StartBatch(5); completed.Tick(); completed.StopBatch();
        Assert.That(completed.State, Is.EqualTo(PlcState.Completed));
        var fault = new SimulatedPlc(); fault.StartBatch(); fault.GuardClosed = false; fault.Tick(); fault.StopBatch();
        Assert.That(fault.State, Is.EqualTo(PlcState.Fault));
    }
    [Test]
    public void ConcurrentTicksDoNotOverrunTarget()
    {
        var plc = new SimulatedPlc(); plc.StartBatch();
        Parallel.For(0, 1000, _ => plc.Tick());
        Assert.That(plc.ActualWeight, Is.EqualTo(500));
        Assert.That(plc.State, Is.EqualTo(PlcState.Completed));
    }
    [Test]
    public void ConcurrentStartsAcceptOnlyOneBatch()
    {
        var plc = new SimulatedPlc(); int accepted = 0;
        Parallel.For(0, 20, _ => { if (plc.StartBatch(250)) Interlocked.Increment(ref accepted); });
        Assert.That(accepted, Is.EqualTo(1));
        Assert.That(plc.TargetWeight, Is.EqualTo(250));
    }
    [Test]
    public void ResetHasNoEffectWhenThereIsNoFault()
    {
        var plc = new SimulatedPlc(); Assert.That(plc.ResetFault(), Is.False);
        plc.StartBatch(); Assert.That(plc.ResetFault(), Is.False);
        Assert.That(plc.State, Is.EqualTo(PlcState.Running));
    }
}
