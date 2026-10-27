using FactoryFlow.Core.Enums;
using FactoryFlow.Core.Models;
using FactoryFlow.Data;
using FactoryFlow.PlcClient.Services;
using System;
using System.ComponentModel;
using System.Configuration;
using System.IO;
using System.Threading.Tasks;
using System.Windows;

namespace FactoryFlow.HMI
{
    public partial class MainWindow : Window
    {
        private readonly PlcCommunicationService _plc;
        private readonly ProductionHistoryService _history;
        private long _lastRevision = -1;
        private PlcMachineSnapshot? _displayed;
        private bool _commandBusy;
        private string? _operationError;
        private bool _closing;
        private bool _closeReady;

        public MainWindow()
        {
            InitializeComponent();
            _plc = new PlcCommunicationService(
                Environment.GetEnvironmentVariable("FACTORYFLOW_OPC_ENDPOINT") ?? ConfigurationManager.AppSettings["PlcEndpoint"] ?? "opc.tcp://localhost:4840",
                namespaceUri: ConfigurationManager.AppSettings["PlcNamespaceUri"] ?? PlcNodeIds.NamespaceUri,
                tagMap: PlcTagMap.Load(ConfigurationManager.AppSettings["PlcTagMapPath"]));
            var connectionString = Environment.GetEnvironmentVariable("FACTORYFLOW_SQL_CONNECTION")
                ?? ConfigurationManager.ConnectionStrings["FactoryFlowDB"]?.ConnectionString
                ?? throw new ConfigurationErrorsException("FactoryFlowDB connection string is missing.");
            _repository = new SqlProductionRepository(connectionString);
            _history = new ProductionHistoryService(_repository,
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "production-outbox"));
            _history.Changed += OnHistoryChanged;
            RecipeComboBox.SelectionChanged += (_, _) => UpdateButtons();
            Loaded += async (_, _) => await LoadRecipesAsync();
            _plc.SnapshotChanged += OnSnapshot;
            _plc.Diagnostic += message => Dispatcher.BeginInvoke(new Action(() => { if (!_closing) Log(message); }));
            Closing += OnClosing;
            InitializeFeatureControls();
            Render(_plc.Snapshot);
        }

        private void OnSnapshot(PlcMachineSnapshot snapshot)
        {
            // SDK callbacks are background-thread callbacks. Never block them waiting for the UI.
            if (_closing || Dispatcher.HasShutdownStarted) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!_closing && snapshot.Revision > _lastRevision)
                {
                    try { _history.Observe(ToObservation(snapshot)); }
                    catch (Exception ex) { DatabaseStatusText.Text = "Logging error: " + ex.Message; }
                    Render(snapshot);
                }
            }));
        }

        private void Render(PlcMachineSnapshot snapshot)
        {
            _lastRevision = snapshot.Revision;
            if (snapshot.IsConnected && _displayed?.IsConnected != true) _operationError = null;
            if (_displayed?.Connection != snapshot.Connection) Log("PLC " + snapshot.Connection);
            if (_displayed?.Status != snapshot.Status && snapshot.Status.HasValue)
                Log("Machine " + ((MachineStatus)snapshot.Status.Value).ToString().ToUpperInvariant());
            _displayed = snapshot;
            ConnectionStatusText.Text = snapshot.Connection;
            ConnectionErrorText.Text = snapshot.Error ?? _operationError ?? "";
            MachineStatusText.Text = snapshot.IsConnected && snapshot.Status.HasValue
                ? ((MachineStatus)snapshot.Status.Value).ToString().ToUpperInvariant() : "UNKNOWN";
            ActualWeightText.Text = snapshot.IsConnected && snapshot.Weight.HasValue
                ? $"Actual Weight: {snapshot.Weight.Value:0.##} kg" : "Actual Weight: — kg";
            EmergencyStopText.Text = "Emergency Stop: " + (snapshot.IsConnected && snapshot.EmergencyStop.HasValue
                ? snapshot.EmergencyStop.Value ? "Active" : "Inactive" : "Unknown");
            GuardClosedText.Text = "Guard: " + (snapshot.IsConnected && snapshot.GuardClosed.HasValue
                ? snapshot.GuardClosed.Value ? "Closed" : "Open" : "Unknown");
            SimEStopCheck.IsChecked = snapshot.EmergencyStop;
            SimGuardCheck.IsChecked = snapshot.GuardClosed;
            UpdateButtons();
        }

        private void UpdateButtons()
        {
            StartBatchButton.IsEnabled = !_closing && !_commandBusy && _displayed?.CanStart == true &&
                _history.CanBeginBatch && _operator != null && RecipeComboBox.SelectedItem is Recipe recipe && recipe.TargetWeight > 0;
            StopBatchButton.IsEnabled = !_closing && !_commandBusy && _displayed?.CanStop == true;
            ConnectButton.IsEnabled = !_closing && _displayed?.Connection == "DISCONNECTED";
            RecipeComboBox.IsEnabled = !_closing && !_commandBusy && !_history.ActiveBatch.HasValue;
            ResetFaultButton.IsEnabled = !_closing && !_commandBusy && _operator != null && _displayed?.CanReset == true;
            SimEStopCheck.IsEnabled = SimGuardCheck.IsEnabled = !_closing && !_commandBusy && _operator != null && _displayed?.IsConnected == true;
            AcknowledgeButton.IsEnabled = _operator != null && _history.IsReady && !_closing;
            SaveRecipeButton.IsEnabled = _operator?.IsSupervisor == true && !_history.ActiveBatch.HasValue;
            SaveOperatorButton.IsEnabled = _operator?.IsSupervisor == true;
            LogoutButton.IsEnabled = _operator != null && !_history.ActiveBatch.HasValue;
            LoginButton.IsEnabled = _operator == null && _history.IsReady;
        }

        private async void Connect_Click(object sender, RoutedEventArgs e)
        {
            _operationError = null;
            ConnectButton.IsEnabled = false;
            try { await _plc.ConnectAsync(); }
            catch (Exception ex) { ShowError(ex); }
            finally { if (!_closing) Render(_plc.Snapshot); }
        }

        private async void StartBatch_Click(object sender, RoutedEventArgs e) => await SendCommandAsync(true);
        private async void StopBatch_Click(object sender, RoutedEventArgs e) => await SendCommandAsync(false);

        private async Task SendCommandAsync(bool start)
        {
            if (_commandBusy || _closing) return;
            _operationError = null;
            _commandBusy = true;
            UpdateButtons();
            bool prepared = false;
            try
            {
                if (start)
                {
                    if (!(RecipeComboBox.SelectedItem is Recipe recipe)) throw new InvalidOperationException("Select a recipe.");
                    if (_operator == null) throw new UnauthorizedAccessException("Sign in before starting production.");
                    await _history.BeginBatchAsync(recipe);
                    prepared = true;
                    await _plc.StartBatchAsync(recipe.TargetWeight);
                    _history.ConfirmStartResult(ToObservation(_plc.Snapshot));
                }
                else
                {
                    // Stop remains available even if SQL is down. Logging must never prevent a stop.
                    try { _history.StopRequested(); } catch (Exception ex) { Log("Stop logging: " + ex.Message); }
                    await _plc.StopBatchAsync();
                    _history.Observe(ToObservation(_plc.Snapshot));
                }
                Log((start ? "Start" : "Stop") + " request acknowledged; display follows actual PLC state.");
            }
            catch (Exception ex)
            {
                if (prepared || !start)
                    try { _history.CommandFailed(start ? "Start" : "Stop", ex.Message); } catch (Exception loggingError) { Log(loggingError.Message); }
                ShowError(ex);
            }
            finally
            {
                _commandBusy = false;
                if (!_closing) Render(_plc.Snapshot);
            }
        }

        private void ShowError(Exception exception)
        {
            if (_closing) return;
            _operationError = exception.Message;
            ConnectionErrorText.Text = exception.Message;
            Log(exception.Message);
        }

        private async void ReloadRecipes_Click(object sender, RoutedEventArgs e) => await LoadRecipesAsync();

        private async Task LoadRecipesAsync()
        {
            ReloadRecipesButton.IsEnabled = false;
            try
            {
                var recipes = await _history.InitializeAsync();
                if (_closing) return;
                RecipeComboBox.ItemsSource = recipes;
                RecipeComboBox.DisplayMemberPath = nameof(Recipe.DisplayName);
                if (recipes.Count > 0) RecipeComboBox.SelectedIndex = 0;
                DatabaseStatusText.Text = _history.StatusText;
                await LoadFeatureDataAsync();
                UpdateButtons();
            }
            catch (Exception ex) { if (!_closing) DatabaseStatusText.Text = "Unavailable: " + ex.Message; }
            finally { if (!_closing) ReloadRecipesButton.IsEnabled = true; }
        }

        private void OnHistoryChanged()
        {
            if (_closing || Dispatcher.HasShutdownStarted) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_closing) return;
                DatabaseStatusText.Text = _history.StatusText + $" (pending events: {_history.PendingCount})";
                UpdateButtons();
            }));
        }

        private static ProductionObservation ToObservation(PlcMachineSnapshot snapshot) => new()
        {
            Revision = snapshot.Revision, Connection = snapshot.Connection, Status = snapshot.Status,
            Weight = snapshot.Weight, EmergencyStop = snapshot.EmergencyStop, GuardClosed = snapshot.GuardClosed,
            StateAndWeightCoherent = snapshot.StateAndWeightCoherent
        };

        private void Log(string message)
        {
            EventListBox.Items.Add($"{DateTime.Now:HH:mm:ss} - {message}");
            // A long-running operator screen should not accumulate unbounded log entries.
            while (EventListBox.Items.Count > 200) EventListBox.Items.RemoveAt(0);
            EventListBox.ScrollIntoView(EventListBox.Items[EventListBox.Items.Count - 1]);
        }

        private async void OnClosing(object? sender, CancelEventArgs e)
        {
            if (_closeReady) return;
            e.Cancel = true;
            if (_closing) return;
            _closing = true;
            _alarmRefresh.Stop();
            UpdateButtons();
            _plc.SnapshotChanged -= OnSnapshot;
            _history.Changed -= OnHistoryChanged;
            try
            {
                try { await _plc.DisposeAsync(); }
                finally { await _history.DisposeAsync(); }
            }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine("PLC shutdown: " + ex); }
            finally { _closeReady = true; Close(); }
        }
    }
}


