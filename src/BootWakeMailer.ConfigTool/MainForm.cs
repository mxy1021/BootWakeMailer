using System.ComponentModel;
using System.ServiceProcess;
using BootWakeMailer.Shared;

namespace BootWakeMailer.ConfigTool;

/// <summary>
/// Local administrator tool for the single BootWakeMailer SMTP configuration.
/// </summary>
internal sealed class MainForm : Form
{
    private const string NotAvailable = "未记录";

    private readonly AppPaths _paths = AppPaths.Default;
    private readonly TextBox _hostTextBox = new();
    private readonly NumericUpDown _portInput = new();
    private readonly ComboBox _securityModeComboBox = new();
    private readonly TextBox _usernameTextBox = new();
    private readonly TextBox _passwordTextBox = new();
    private readonly TextBox _fromTextBox = new();
    private readonly TextBox _toTextBox = new();
    private readonly Button _saveButton = new();
    private readonly Button _testButton = new();
    private readonly Button _refreshButton = new();
    private readonly Button _retryButton = new();
    private readonly Label _serviceStatusValue = CreateStatusValue();
    private readonly Label _pendingCountValue = CreateStatusValue();
    private readonly Label _lastAttemptValue = CreateStatusValue();
    private readonly Label _lastSuccessValue = CreateStatusValue();
    private readonly TextBox _lastErrorTextBox = new();
    private CancellationTokenSource? _testCancellation;

    public MainForm()
    {
        Text = "BootWakeMailer 配置";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(680, 650);
        Size = new Size(760, 700);
        FormBorderStyle = FormBorderStyle.Sizable;

        Controls.Add(CreateRootLayout());
        Load += (_, _) => LoadInitialState();
        FormClosing += (_, _) => _testCancellation?.Cancel();
    }

    private Control CreateRootLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 1,
            RowCount = 3,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(CreateSmtpGroup(), 0, 0);
        root.Controls.Add(CreateButtonRow(), 0, 1);
        root.Controls.Add(CreateStatusGroup(), 0, 2);
        return root;
    }

    private Control CreateSmtpGroup()
    {
        var group = new GroupBox
        {
            Text = "SMTP 配置",
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(12),
        };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 7,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _portInput.Minimum = 1;
        _portInput.Maximum = 65535;
        _portInput.Value = 587;
        _portInput.Width = 120;

        _securityModeComboBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _securityModeComboBox.DataSource = Enum.GetValues<SmtpSecurityMode>();

        _passwordTextBox.UseSystemPasswordChar = true;

        AddEditorRow(layout, 0, "Host", _hostTextBox);
        AddEditorRow(layout, 1, "Port", _portInput);
        AddEditorRow(layout, 2, "TLS/SSL 模式", _securityModeComboBox);
        AddEditorRow(layout, 3, "Username", _usernameTextBox);
        AddEditorRow(layout, 4, "Password", _passwordTextBox);
        AddEditorRow(layout, 5, "From", _fromTextBox);
        AddEditorRow(layout, 6, "To", _toTextBox);
        group.Controls.Add(layout);
        return group;
    }

    private Control CreateButtonRow()
    {
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(0, 12, 0, 12),
            WrapContents = true,
        };

        ConfigureButton(_saveButton, "保存配置", (_, _) => SaveConfiguration());
        ConfigureButton(_testButton, "发送测试邮件", async (_, _) => await SendTestMailAsync());
        ConfigureButton(_refreshButton, "刷新状态", (_, _) => RefreshStatus(showErrors: true));
        ConfigureButton(_retryButton, "立即重试", (_, _) => RequestImmediateRetry());
        panel.Controls.AddRange([_saveButton, _testButton, _refreshButton, _retryButton]);
        return panel;
    }

    private Control CreateStatusGroup()
    {
        var group = new GroupBox
        {
            Text = "状态",
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
        };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 5,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        AddStatusRow(layout, 0, "Windows Service 状态", _serviceStatusValue);
        AddStatusRow(layout, 1, "Pending 邮件数量", _pendingCountValue);
        AddStatusRow(layout, 2, "上次发送尝试时间", _lastAttemptValue);
        AddStatusRow(layout, 3, "上次发送成功时间", _lastSuccessValue);

        _lastErrorTextBox.Dock = DockStyle.Fill;
        _lastErrorTextBox.Multiline = true;
        _lastErrorTextBox.ReadOnly = true;
        _lastErrorTextBox.ScrollBars = ScrollBars.Vertical;
        _lastErrorTextBox.BackColor = SystemColors.Window;
        AddStatusRow(layout, 4, "最近错误", _lastErrorTextBox);
        group.Controls.Add(layout);
        return group;
    }

    private void LoadInitialState()
    {
        try
        {
            var config = ConfigStore.Load(_paths.ConfigFilePath);
            if (config is not null)
            {
                _hostTextBox.Text = config.Smtp.Host;
                _portInput.Value = Math.Clamp(config.Smtp.Port, 1, 65535);
                _securityModeComboBox.SelectedItem = Enum.IsDefined(config.Smtp.SecurityMode)
                    ? config.Smtp.SecurityMode
                    : SmtpSecurityMode.StartTls;
                _usernameTextBox.Text = config.Smtp.Username;
                _passwordTextBox.Text = SecretProtector.Unprotect(config.Smtp.EncryptedPassword);
                _fromTextBox.Text = config.FromAddress;
                _toTextBox.Text = config.ToAddress;
            }
        }
        catch (Exception exception)
        {
            ShowError("读取配置失败", exception);
        }

        RefreshStatus(showErrors: false);
    }

    private void SaveConfiguration()
    {
        try
        {
            var config = BuildCurrentConfig();
            ConfigStore.Save(_paths.ConfigFilePath, config);
            MessageBox.Show(this, "配置已保存。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception exception)
        {
            ShowError("保存配置失败", exception);
        }
    }

    private async Task SendTestMailAsync()
    {
        _testCancellation?.Dispose();
        _testCancellation = new CancellationTokenSource();
        SetActionsEnabled(false);

        try
        {
            var config = BuildCurrentConfig();
            await TestMail.SendAsync(new SmtpMailSender(), config, cancellationToken: _testCancellation.Token);
            MessageBox.Show(
                this,
                "测试邮件已被 SMTP 服务器接受。该邮件未加入正式队列。",
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (OperationCanceledException) when (_testCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ShowError("测试邮件发送失败", exception);
        }
        finally
        {
            SetActionsEnabled(true);
        }
    }

    private AppConfig BuildCurrentConfig()
    {
        var config = new AppConfig
        {
            Smtp = new SmtpSettings
            {
                Host = _hostTextBox.Text.Trim(),
                Port = decimal.ToInt32(_portInput.Value),
                SecurityMode = (SmtpSecurityMode)(_securityModeComboBox.SelectedItem ?? SmtpSecurityMode.StartTls),
                Username = _usernameTextBox.Text.Trim(),
                EncryptedPassword = SecretProtector.Protect(_passwordTextBox.Text),
            },
            FromAddress = _fromTextBox.Text.Trim(),
            ToAddress = _toTextBox.Text.Trim(),
        };

        var problems = ConfigValidator.Validate(config);
        if (problems.Count > 0)
        {
            throw new MailConfigurationException(string.Join(Environment.NewLine, problems));
        }

        return config;
    }

    private void RefreshStatus(bool showErrors)
    {
        var errors = new List<string>();
        _serviceStatusValue.Text = ReadServiceStatus(errors);

        QueueDocument? queue = null;
        StatusDocument? status = null;
        try
        {
            queue = QueueStore.Load(_paths.QueueFilePath);
            _pendingCountValue.Text = queue.Items.Count(item => item is not null).ToString();
        }
        catch (Exception exception)
        {
            _pendingCountValue.Text = "读取失败";
            errors.Add($"读取 queue.json 失败：{ErrorText.Describe(exception)}");
        }

        try
        {
            status = StatusStore.Load(_paths.StatusFilePath);
            _lastSuccessValue.Text = FormatTimestamp(status.LastSuccessfulSendAtUtc);
        }
        catch (Exception exception)
        {
            _lastSuccessValue.Text = "读取失败";
            errors.Add($"读取 status.json 失败：{ErrorText.Describe(exception)}");
        }

        var pendingAttempt = queue?.Items
            .Where(item => item is not null)
            .Select(item => item.LastAttemptAtUtc)
            .Where(timestamp => timestamp.HasValue)
            .Max();
        _lastAttemptValue.Text = FormatTimestamp(Latest(pendingAttempt, status?.LastSuccessfulSendAtUtc));

        if (status?.LastError is { } lastError)
        {
            _lastErrorTextBox.Text =
                $"{FormatTimestamp(lastError.OccurredAtUtc)}  [{lastError.Operation}/{lastError.Type}]{Environment.NewLine}{lastError.Message}";
        }
        else
        {
            _lastErrorTextBox.Text = NotAvailable;
        }

        if (errors.Count > 0 && showErrors)
        {
            MessageBox.Show(this, string.Join(Environment.NewLine, errors), "刷新状态失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private string ReadServiceStatus(List<string> errors)
    {
        try
        {
            using var controller = new ServiceController(AppConstants.ServiceName);
            controller.Refresh();
            return controller.Status switch
            {
                ServiceControllerStatus.Running => "运行中",
                ServiceControllerStatus.Stopped => "已停止",
                ServiceControllerStatus.Paused => "已暂停",
                ServiceControllerStatus.StartPending => "正在启动",
                ServiceControllerStatus.StopPending => "正在停止",
                ServiceControllerStatus.ContinuePending => "正在继续",
                ServiceControllerStatus.PausePending => "正在暂停",
                _ => controller.Status.ToString(),
            };
        }
        catch (InvalidOperationException exception)
        {
            errors.Add($"读取服务状态失败：{ErrorText.Describe(exception)}");
            return "未安装或无法访问";
        }
        catch (Win32Exception exception)
        {
            errors.Add($"读取服务状态失败：{ErrorText.Describe(exception)}");
            return "无法访问";
        }
    }

    private void RequestImmediateRetry()
    {
        try
        {
            using var controller = new ServiceController(AppConstants.ServiceName);
            controller.Refresh();
            if (controller.Status != ServiceControllerStatus.Running)
            {
                throw new InvalidOperationException("BootWakeMailer Windows Service 当前未运行。");
            }

            controller.ExecuteCommand(AppConstants.ImmediateRetryCommand);
            MessageBox.Show(this, "已请求服务立即重试 Pending 邮件。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            RefreshStatus(showErrors: false);
        }
        catch (Exception exception)
        {
            ShowError("立即重试请求失败", exception);
        }
    }

    private void SetActionsEnabled(bool enabled)
    {
        _saveButton.Enabled = enabled;
        _testButton.Enabled = enabled;
        _refreshButton.Enabled = enabled;
        _retryButton.Enabled = enabled;
        UseWaitCursor = !enabled;
    }

    private void ShowError(string title, Exception exception) =>
        MessageBox.Show(this, ErrorText.Describe(exception), title, MessageBoxButtons.OK, MessageBoxIcon.Error);

    private static DateTime? Latest(DateTime? first, DateTime? second)
    {
        if (!first.HasValue)
        {
            return second;
        }

        if (!second.HasValue)
        {
            return first;
        }

        return first.Value >= second.Value ? first : second;
    }

    private static string FormatTimestamp(DateTime? timestampUtc) =>
        timestampUtc.HasValue
            ? timestampUtc.Value.ToUniversalTime().ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz")
            : NotAvailable;

    private static Label CreateStatusValue() => new()
    {
        AutoSize = true,
        Anchor = AnchorStyles.Left,
    };

    private static void ConfigureButton(Button button, string text, EventHandler handler)
    {
        button.Text = text;
        button.AutoSize = true;
        button.MinimumSize = new Size(120, 34);
        button.Click += handler;
    }

    private static void AddEditorRow(TableLayoutPanel layout, int row, string labelText, Control editor)
    {
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var label = new Label
        {
            Text = labelText,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(3, 9, 3, 3),
        };
        editor.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        editor.Margin = new Padding(3, 4, 3, 4);
        layout.Controls.Add(label, 0, row);
        layout.Controls.Add(editor, 1, row);
    }

    private static void AddStatusRow(TableLayoutPanel layout, int row, string labelText, Control value)
    {
        var label = new Label
        {
            Text = labelText,
            AutoSize = true,
            Anchor = AnchorStyles.Left | AnchorStyles.Top,
            Margin = new Padding(3, 7, 3, 7),
        };
        value.Margin = new Padding(3, 7, 3, 7);
        layout.Controls.Add(label, 0, row);
        layout.Controls.Add(value, 1, row);
    }
}
