using FactoryFlow.Core.Models;
using FactoryFlow.Data;
using Microsoft.Win32;
using System;
using System.Data;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace FactoryFlow.HMI
{
    public partial class MainWindow
    {
        private SqlProductionRepository _repository = null!;
        private OperatorProfile? _operator;
        private int? _editingRecipeId;
        private bool _alarmsBusy;
        private readonly DispatcherTimer _alarmRefresh = new() { Interval = TimeSpan.FromSeconds(3) };
        private DataTable? _historyRows;

        private void InitializeFeatureControls()
        {
            HistoryFromPicker.SelectedDate = DateTime.Today.AddDays(-7);
            HistoryToPicker.SelectedDate = DateTime.Today;
            HistoryStatusCombo.ItemsSource = new[] { "All", "Starting", "Running", "Completed", "Stopped", "Fault", "Interrupted", "Rejected", "Unconfirmed" };
            HistoryStatusCombo.SelectedIndex = 0;
            OperatorRoleCombo.ItemsSource = new[] { "Operator", "Supervisor" };
            OperatorRoleCombo.SelectedIndex = 0;
            _alarmRefresh.Tick += async (_, _) =>
            {
                if (!_closing && _history.IsReady && FeatureTabs.SelectedIndex == 2) await RefreshAlarmsAsync();
            };
            _alarmRefresh.Start();
        }

        private async Task LoadFeatureDataAsync()
        {
            RecipeGrid.ItemsSource = (await _repository.GetAllRecipesAsync()).DefaultView;
            var operators = await _repository.GetOperatorsAsync();
            OperatorGrid.ItemsSource = operators.DefaultView;
            BootstrapButton.Visibility = operators.Rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void Bootstrap_Click(object sender, RoutedEventArgs e)
        {
            try { await _repository.BootstrapFirstSupervisorAsync(); await LoadFeatureDataAsync(); await SignInAsync(); }
            catch (Exception ex) { ShowError(ex); }
        }
        private async void Login_Click(object sender, RoutedEventArgs e)
        {
            try { await SignInAsync(); } catch (Exception ex) { ShowError(ex); }
        }
        private async Task SignInAsync()
        {
            _operator = await _repository.GetCurrentOperatorAsync()
                ?? throw new UnauthorizedAccessException("This Windows account is not registered. Use first-run setup, or ask a Supervisor to grant access.");
            _history.RecordOperatorAction("OperatorSignedIn", _operator.Role, _operator.WindowsAccount);
            OperatorStatusText.Text = $"{_operator.DisplayName} ({_operator.Role}) — {_operator.WindowsAccount}";
            UpdateButtons();
            Log("Operator signed in: " + _operator.WindowsAccount);
        }
        private void Logout_Click(object sender, RoutedEventArgs e)
        {
            if (_history.ActiveBatch.HasValue) { ShowError(new InvalidOperationException("Finish or stop the tracked batch before signing out.")); return; }
            try { if (_operator != null) _history.RecordOperatorAction("OperatorSignedOut", "Operator signed out.", _operator.WindowsAccount); }
            catch (Exception ex) { ShowError(ex); }
            _operator = null; OperatorStatusText.Text = "Operator: signed out"; UpdateButtons();
        }

        private async void ResetFault_Click(object sender, RoutedEventArgs e)
        {
            if (_commandBusy || _operator == null) return;
            _commandBusy = true; UpdateButtons();
            try
            {
                await _repository.RequireOperatorAsync();
                _history.RecordOperatorAction("FaultResetRequested", "Operator requested a fault reset.", _operator.WindowsAccount);
                await _plc.ResetFaultAsync();
                _history.RecordOperatorAction("FaultResetConfirmed", "PLC confirmed Idle after reset.", _operator.WindowsAccount);
                Log("Fault reset confirmed by PLC.");
            }
            catch (Exception ex) { ShowError(ex); }
            finally { _commandBusy = false; Render(_plc.Snapshot); }
        }

        private async void SimInterlock_Click(object sender, RoutedEventArgs e)
        {
            if (_operator == null) return;
            try
            {
                await _repository.RequireOperatorAsync();
                _history.RecordOperatorAction("SimulatorInterlockWrite", ReferenceEquals(sender, SimEStopCheck) ? "E-stop simulation control" : "Guard simulation control", _operator.WindowsAccount);
                await _plc.WriteTagAsync(ReferenceEquals(sender, SimEStopCheck) ? "EStopActive" : "GuardClosed",
                    ReferenceEquals(sender, SimEStopCheck) ? SimEStopCheck.IsChecked == true : SimGuardCheck.IsChecked == true);
            }
            catch (Exception ex) { ShowError(ex); }
            finally { Render(_plc.Snapshot); }
        }

        private async void RefreshHistory_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var from = (HistoryFromPicker.SelectedDate ?? DateTime.Today).Date;
                var until = (HistoryToPicker.SelectedDate ?? DateTime.Today).Date.AddDays(1);
                if (until <= from) throw new ArgumentException("Choose an end date on or after the start date.");
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var status = HistoryStatusCombo.SelectedItem as string;
                _historyRows = await _repository.GetBatchHistoryAsync(from.ToUniversalTime(), until.ToUniversalTime(), status == "All" ? null : status);
                HistoryGrid.ItemsSource = _historyRows.DefaultView;
                HistorySummaryText.Text = $"{_historyRows.Rows.Count} rows (maximum 1000). Query + transfer: {watch.ElapsedMilliseconds} ms. Timestamp columns are UTC.";
            }
            catch (Exception ex) { ShowError(ex); }
        }
        private async void ExportHistory_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_historyRows == null) throw new InvalidOperationException("Refresh batch history before exporting.");
                var dialog = new SaveFileDialog { Filter = "CSV report (*.csv)|*.csv", FileName = "FactoryFlow-Batches-" + DateTime.Now.ToString("yyyyMMdd") + ".csv" };
                if (dialog.ShowDialog(this) == true)
                {
                    var data = _historyRows.Copy();
                    await Task.Run(() => CsvReport.Write(data, dialog.FileName));
                    Log("Batch report exported: " + dialog.FileName);
                }
            }
            catch (Exception ex) { ShowError(ex); }
        }

        private async void RefreshAlarms_Click(object sender, RoutedEventArgs e) => await RefreshAlarmsAsync();
        private async Task RefreshAlarmsAsync()
        {
            if (_alarmsBusy) return;
            _alarmsBusy = true;
            try
            {
                var rows = await _repository.GetAlarmsAsync(IncludeClosedAlarmsCheck.IsChecked == true);
                // Read selection after the await: the operator may select while SQL is in flight.
                var selectedId = (AlarmGrid.SelectedItem as DataRowView)?["AlarmId"];
                AlarmGrid.ItemsSource = rows.DefaultView;
                if (selectedId != null)
                    foreach (DataRowView row in rows.DefaultView)
                        if (Equals(row["AlarmId"], selectedId)) { AlarmGrid.SelectedItem = row; break; }
            }
            catch (Exception ex) { ShowError(ex); }
            finally { _alarmsBusy = false; }
        }
        private async void AcknowledgeAlarm_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_operator == null) throw new UnauthorizedAccessException("Sign in before acknowledging an alarm.");
                if (!(AlarmGrid.SelectedItem is DataRowView row)) throw new InvalidOperationException("Select an alarm.");
                await _repository.AcknowledgeAlarmAsync((long)row["AlarmId"]);
                _operationError = null;
                ConnectionErrorText.Text = _plc.Snapshot.Error ?? "";
                await RefreshAlarmsAsync();
            }
            catch (Exception ex) { ShowError(ex); }
        }

        private void NewRecipe_Click(object sender, RoutedEventArgs e)
        { _editingRecipeId = null; RecipeNameEdit.Text = ""; RecipeTargetEdit.Text = ""; RecipeActiveCheck.IsChecked = true; }
        private void RecipeGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!(RecipeGrid.SelectedItem is DataRowView row)) return;
            _editingRecipeId = (int)row["RecipeId"]; RecipeNameEdit.Text = (string)row["RecipeName"];
            RecipeTargetEdit.Text = Convert.ToDouble(row["TargetWeight"]).ToString(CultureInfo.CurrentCulture);
            RecipeActiveCheck.IsChecked = (bool)row["IsActive"];
        }
        private async void SaveRecipe_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_history.ActiveBatch.HasValue) throw new InvalidOperationException("Finish the active batch before editing recipes.");
                if (!double.TryParse(RecipeTargetEdit.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double target)) throw new ArgumentException("Enter a numeric target weight.");
                await _repository.SaveRecipeAsync(_editingRecipeId, RecipeNameEdit.Text, target, RecipeActiveCheck.IsChecked == true);
                await LoadRecipesAsync();
            }
            catch (Exception ex) { ShowError(ex); }
        }
        private async void RefreshRecipeGrid_Click(object sender, RoutedEventArgs e)
        { try { await LoadFeatureDataAsync(); } catch (Exception ex) { ShowError(ex); } }
        private void OperatorGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!(OperatorGrid.SelectedItem is DataRowView row)) return;
            OperatorAccountEdit.Text = (string)row["WindowsAccount"]; OperatorNameEdit.Text = (string)row["DisplayName"];
            OperatorRoleCombo.SelectedItem = (string)row["Role"]; OperatorEnabledCheck.IsChecked = (bool)row["IsEnabled"];
        }
        private async void SaveOperator_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await _repository.SaveOperatorAsync(OperatorAccountEdit.Text, OperatorNameEdit.Text,
                    OperatorRoleCombo.SelectedItem as string ?? "Operator", OperatorEnabledCheck.IsChecked == true);
                await LoadFeatureDataAsync();
            }
            catch (Exception ex) { ShowError(ex); }
        }
    }
}
