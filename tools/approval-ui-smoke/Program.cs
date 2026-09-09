using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;
using MessagingRuntime;
using MessagingRuntime.Host;
using MessagingRuntime.Storage;

// Isolated UI smoke: in-memory drafts only; no provider, no real messages, no live database.
using var store = new MessageStore(":memory:");
var notifier = new DesktopApprovalNotifier(store);
var output = Path.GetFullPath(args.Single());
Directory.CreateDirectory(output);
foreach (var action in new[] { "approve", "reject", "dismiss" })
{
    var request = new SendRequest(Guid.NewGuid().ToString(), "smoke-test", "test-only", "test-recipient",
        "【仅界面测试，不会发送】\r\n这是一条供用户审阅的测试草稿。\r\n" + string.Join("\r\n", Enumerable.Range(1, 30).Select(i => $"第 {i} 行：验证长消息可滚动查看。")), ConversationTitle: "测试收件人");
    store.RegisterSend(request, "pending");
    notifier.Notify(request);
    notifier.Notify(request);
    var deadline = DateTime.UtcNow.AddSeconds(10);
    while (notifier.Describe(request.RequestId).State == "opening" && DateTime.UtcNow < deadline) Thread.Sleep(25);
    if (notifier.Describe(request.RequestId).State != "visible") throw new Exception("Approval window failed to open.");
    var forms = Application.OpenForms.Cast<Form>().ToArray();
    if (forms.Length != 1) throw new Exception("Duplicate approval window.");
    var form = forms.Single();
    form.Invoke(() =>
    {
        IEnumerable<Control> Descendants(Control parent) => parent.Controls.Cast<Control>().SelectMany(c => new[] { c }.Concat(Descendants(c)));
        var controls = Descendants(form).ToArray();
        var text = controls.OfType<TextBox>().Single();
        if (!text.ReadOnly || text.Text != request.Text) throw new Exception("Draft not displayed exactly.");
        if (action == "approve")
        {
            using var bitmap = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            bitmap.Save(Path.Combine(output, "approval-window.png"), ImageFormat.Png);
        }
        if (action == "dismiss") form.Close();
        else controls.OfType<Button>().Single(b => b.Text == (action == "approve" ? "批准此消息" : "拒绝")).PerformClick();
    });
    var expected = action == "approve" ? "approved" : action == "reject" ? "rejected" : "pending";
    if (store.ReviewSend(request.RequestId).Status != expected) throw new Exception("Unexpected approval state.");
    if (action == "dismiss" && notifier.Describe(request.RequestId).State != "dismissed") throw new Exception("Dismissed state missing.");
    Console.WriteLine($"{action}: passed (duplicate prompt suppressed, full text preserved, state={expected})");
}
