using FactoryFlow.PlcClient.Services;
using System;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace FactoryFlow.Diagnostics
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            if (Array.IndexOf(args, "--prepare-certificates") >= 0)
            {
                using var preparation = new CertificatePreparation();
                preparation.Run(); return;
            }
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false); Application.Run(new DiagnosticsForm());
        }
        private sealed class CertificatePreparation : IDisposable
        {
            private readonly PlcCommunicationService _client = new("opc.tcp://localhost:4840",
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "pki"), "FactoryFlow.Diagnostics");
            public void Run() => _client.PrepareCertificateAsync().GetAwaiter().GetResult();
            public void Dispose() => _client.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
    internal sealed class DiagnosticsForm : Form
    {
        private readonly TextBox _endpoint = new() { Text = "opc.tcp://localhost:4840", Width = 280 };
        private readonly TextBox _namespace = new() { Text = PlcNodeIds.NamespaceUri, Width = 280 };
        private readonly TextBox _map = new() { Width = 280 };
        private readonly TextBox _tag = new() { Text = "Status", Width = 180 };
        private readonly Button _connect = new() { Text = "Connect", AutoSize = true };
        private readonly DataGridView _values = new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill };
        private readonly ListBox _events = new() { Dock = DockStyle.Fill };
        private PlcCommunicationService? _client;
        private bool _closing;
        private bool _closed;
        public DiagnosticsForm()
        {
            Text = "FactoryFlow - WinForms PLC Diagnostics"; Width = 1000; Height = 650;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new Padding(12) };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 150)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            var settings = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true };
            settings.Controls.Add(new Label { Text = "Endpoint", AutoSize = true, Margin = new Padding(5, 8, 5, 5) }); settings.Controls.Add(_endpoint);
            settings.Controls.Add(new Label { Text = "Namespace URI", AutoSize = true, Margin = new Padding(5, 8, 5, 5) }); settings.Controls.Add(_namespace);
            settings.SetFlowBreak(_namespace, true);
            settings.Controls.Add(new Label { Text = "Tag map XML (optional)", AutoSize = true, Margin = new Padding(5, 8, 5, 5) }); settings.Controls.Add(_map);
            settings.Controls.Add(_connect);
            var disconnect = new Button { Text = "Disconnect", AutoSize = true }; settings.Controls.Add(disconnect);
            settings.SetFlowBreak(disconnect, true);
            settings.Controls.Add(new Label { Text = "Read tag alias", AutoSize = true, Margin = new Padding(5, 8, 5, 5) }); settings.Controls.Add(_tag);
            var read = new Button { Text = "Read / validate", AutoSize = true }; settings.Controls.Add(read);
            settings.Controls.Add(new Label { Text = "Read-only diagnostics. Production commands stay in the operator HMI.", AutoSize = true, Margin = new Padding(5, 10, 5, 5) });
            _values.Columns.Add("Tag", "Tag"); _values.Columns.Add("Value", "Observed value");
            layout.Controls.Add(settings, 0, 0); layout.Controls.Add(_values, 0, 1); layout.Controls.Add(_events, 0, 2); Controls.Add(layout);
            _connect.Click += async (_, _) => await ConnectAsync();
            disconnect.Click += async (_, _) =>
            {
                try { if (_client != null) await _client.DisconnectAsync(); }
                catch (Exception ex) { Log(ex.Message); }
            };
            read.Click += async (_, _) =>
            {
                try { if (_client == null) throw new InvalidOperationException("Connect first."); Log(_tag.Text + " = " + await _client.ReadTagAsync(_tag.Text)); }
                catch (Exception ex) { Log(ex.Message); }
            };
            FormClosing += async (_, e) =>
            {
                if (_closed) return; e.Cancel = true; if (_closing) return; _closing = true;
                try { if (_client != null) await _client.DisposeAsync(); }
                finally { _closed = true; Close(); }
            };
        }
        private async Task ConnectAsync()
        {
            _connect.Enabled = false;
            try
            {
                if (_client != null) await _client.DisposeAsync();
                _client = new PlcCommunicationService(_endpoint.Text, applicationName: "FactoryFlow.Diagnostics",
                    namespaceUri: _namespace.Text, tagMap: PlcTagMap.Load(_map.Text));
                _client.Diagnostic += text => Post(() => Log(text));
                _client.SnapshotChanged += snapshot => Post(() =>
                {
                    _values.Rows.Clear();
                    _values.Rows.Add("Connection", snapshot.Connection);
                    _values.Rows.Add("Status", snapshot.Status?.ToString() ?? "Unknown");
                    _values.Rows.Add("ActualWeight", snapshot.Weight?.ToString() ?? "Unknown");
                    _values.Rows.Add("EStopActive", snapshot.EmergencyStop?.ToString() ?? "Unknown");
                    _values.Rows.Add("GuardClosed", snapshot.GuardClosed?.ToString() ?? "Unknown");
                    if (snapshot.Error != null) Log(snapshot.Error);
                });
                await _client.ConnectAsync();
            }
            catch (Exception ex) { Log(ex.Message); }
            finally { if (!_closing) _connect.Enabled = true; }
        }
        private void Post(Action action) { if (!_closing && !IsDisposed && IsHandleCreated) BeginInvoke(action); }
        private void Log(string text)
        {
            _events.Items.Add(DateTime.Now.ToString("HH:mm:ss") + " " + text);
            while (_events.Items.Count > 200) _events.Items.RemoveAt(0);
        }
    }
}
