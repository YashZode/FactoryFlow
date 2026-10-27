USE FactoryFlowDB;
GO
-- Batch history: UTC timestamps, selected recipe, outcome and measured final weight.
SELECT TOP (100)
    b.BatchId, r.RecipeName, l.LineName, b.Status,
    b.TargetWeight AS TargetWeightKg, b.ActualWeight AS FinalWeightKg,
    b.StartedAt AS StartedAtUtc, b.CompletedAt AS CompletedAtUtc, b.EndedAt AS EndedAtUtc,
    b.CorrelationId,b.OperatorAccount,b.RecipeNameSnapshot
FROM dbo.Batches b
JOIN dbo.Recipes r ON r.RecipeId=b.RecipeId
JOIN dbo.ProductionLines l ON l.LineId=b.LineId
ORDER BY b.BatchId DESC;

-- Audit events: connection changes, machine states, interlocks and batch lifecycle.
SELECT TOP (200)
    e.EventId, e.MachineId AS LineId, b.BatchId, e.EventType, e.EventValue,
    e.OccurredAt AS OccurredAtUtc, e.EventKey,e.OperatorAccount,e.Source
FROM dbo.MachineEvents e
LEFT JOIN dbo.Batches b ON b.CorrelationId=e.BatchCorrelationId
ORDER BY e.EventId DESC;

SELECT LineId,LineName,Status FROM dbo.ProductionLines;
SELECT RecipeId,RecipeName,TargetWeight FROM dbo.Recipes;

SELECT TOP(100) * FROM dbo.vwFactoryFlowAlarmHistory ORDER BY AlarmId DESC;
SELECT WindowsAccount,DisplayName,Role,IsEnabled FROM dbo.Operators;
SELECT Source,COUNT_BIG(*) AS EventCount,MAX(OccurredAt) AS LastEventUtc
FROM dbo.MachineEvents GROUP BY Source;
