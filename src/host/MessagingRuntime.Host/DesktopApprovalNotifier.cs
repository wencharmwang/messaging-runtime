using System.Collections.Concurrent;
using System.Drawing;
using System.Windows.Forms;
using MessagingRuntime.Storage;

namespace MessagingRuntime.Host;

/// <summary>Runtime-owned UI; MCP clients cannot approve a proposal.</summary>
public sealed class DesktopApprovalNotifier(MessageStore store) : IApprovalNotifier
{
    private readonly ConcurrentDictionary<string, string> _states = new();

    public ApprovalInfo Notify(SendRequest request)
    {
        // One prompt per request in this host lifetime. Retrying a send never spawns duplicate dialogs.
        if (_states.TryAdd(request.RequestId, "opening"))
        {
            try
            {
                if (!Environment.UserInteractive) throw new InvalidOperationException();
                var thread = new Thread(() => Show(request)) { IsBackground = true, Name = "Messaging approval" };
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
            }
            catch { _states[request.RequestId] = "unavailable"; }
        }
        return Describe(request.RequestId);
    }

    public ApprovalInfo Describe(string requestId)
    {
        var state = _states.GetValueOrDefault(requestId, "not_opened");
        return state is "opening" or "visible"
            ? new("runtime_desktop", state,
                "Review the recipient and full message in the Messaging Runtime approval window (not a WorkBuddy permission popup). Approve or reject there. Stop send retries; after user action query get_send_status. If approved, retry the identical request_id and payload. If the window is not visible, use the local --review-request and --approve-request/--reject-request commands with the same config/data directory.")
            : MessagingService.ManualApproval(state);
    }

    private void Show(SendRequest request)
    {
        try
        {
            using var form = new Form
            {
                Text = "Messaging Runtime · 消息发送审批", Width = 660, Height = 530,
                MinimumSize = new Size(580, 440), StartPosition = FormStartPosition.CenterScreen,
                TopMost = true, Font = new Font("Microsoft YaHei UI", 10), AutoScaleMode = AutoScaleMode.Dpi
            };
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), RowCount = 4, ColumnCount = 1 };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(new Label
            {
                AutoSize = true, Dock = DockStyle.Fill, UseMnemonic = false, Padding = new Padding(0, 0, 0, 12),
                Text = $"Agent 提议发送消息，请审阅：\n收件人：{request.ConversationTitle ?? request.ConversationId}\n平台：{request.Provider}  ·  Agent：{request.AgentId}\n会话 ID：{request.ConversationId}" +
                    (request.ReplyTo is null ? "" : $"\n引用消息 ID：{request.ReplyTo}")
            });
            layout.Controls.Add(new TextBox
            {
                Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
                Text = request.Text, BackColor = Color.White
            });
            layout.Controls.Add(new Label
            {
                AutoSize = true, Dock = DockStyle.Fill, UseMnemonic = false, Padding = new Padding(0, 12, 0, 12),
                Text = $"请求：{request.RequestId}\n批准后由 agent 继续发送。关闭窗口将保留待审批状态。"
            });
            var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
            var approve = new Button { Text = "批准此消息", AutoSize = true };
            var reject = new Button { Text = "拒绝", AutoSize = true };
            void Decide(bool allowed)
            {
                try
                {
                    store.DecideSend(request.RequestId, allowed);
                    _states[request.RequestId] = allowed ? "approved" : "rejected";
                    form.Close();
                }
                catch
                {
                    _states[request.RequestId] = "decision_failed";
                    MessageBox.Show(form, "请求状态已变化或保存失败。请查询发送状态，不要创建新请求重发。", "无法保存审批");
                    form.Close();
                }
            }
            approve.Click += (_, _) => Decide(true);
            reject.Click += (_, _) => Decide(false);
            buttons.Controls.Add(approve);
            buttons.Controls.Add(reject);
            layout.Controls.Add(buttons);
            form.Controls.Add(layout);
            // Enter must not approve a message accidentally.
            form.Shown += (_, _) => { _states[request.RequestId] = "visible"; reject.Focus(); };
            form.FormClosed += (_, _) => _states.TryUpdate(request.RequestId, "dismissed", "visible");
            Application.Run(form);
        }
        catch { _states[request.RequestId] = "unavailable"; }
    }
}
