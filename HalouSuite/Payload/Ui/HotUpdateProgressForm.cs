using System;
using System.Drawing;
using System.Windows.Forms;

namespace HalouSuite.Payload
{
    // 简单的下载进度对话框：进度条 + 百分比 + 字节数 + 取消按钮（暂时不接 cancel）
    internal sealed class HotUpdateProgressForm : Form
    {
        private readonly ProgressBar _bar;
        private readonly Label _percentLabel;
        private readonly Label _bytesLabel;
        private readonly Label _statusLabel;
        private readonly Button _closeButton;

        public HotUpdateProgressForm(string title)
        {
            Text = title ?? "正在下载更新";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(420, 150);

            _statusLabel = new Label
            {
                Location = new Point(16, 14),
                Size = new Size(388, 20),
                Text = "正在下载新版本…"
            };
            Controls.Add(_statusLabel);

            _bar = new ProgressBar
            {
                Location = new Point(16, 40),
                Size = new Size(388, 22),
                Minimum = 0,
                Maximum = 100,
                Style = ProgressBarStyle.Continuous
            };
            Controls.Add(_bar);

            _percentLabel = new Label
            {
                Location = new Point(16, 68),
                Size = new Size(80, 18),
                Text = "0 %"
            };
            Controls.Add(_percentLabel);

            _bytesLabel = new Label
            {
                Location = new Point(100, 68),
                Size = new Size(304, 18),
                TextAlign = ContentAlignment.MiddleRight,
                Text = ""
            };
            Controls.Add(_bytesLabel);

            _closeButton = new Button
            {
                Location = new Point(322, 110),
                Size = new Size(82, 28),
                Text = "关闭",
                Enabled = false
            };
            _closeButton.Click += delegate { Close(); };
            Controls.Add(_closeButton);
        }

        public void ReportProgress(int percent, long received, long total)
        {
            if (InvokeRequired) { BeginInvoke(new Action<int, long, long>(ReportProgress), percent, received, total); return; }
            try
            {
                if (percent < 0) percent = 0;
                if (percent > 100) percent = 100;
                _bar.Value = percent;
                _percentLabel.Text = percent + " %";
                _bytesLabel.Text = FormatSize(received) + (total > 0 ? " / " + FormatSize(total) : "");
            }
            catch { }
        }

        public void SetStatus(string text)
        {
            if (InvokeRequired) { BeginInvoke(new Action<string>(SetStatus), text); return; }
            _statusLabel.Text = text ?? "";
        }

        public void MarkFinished(bool ok)
        {
            if (InvokeRequired) { BeginInvoke(new Action<bool>(MarkFinished), ok); return; }
            try
            {
                if (ok) { _bar.Value = 100; _percentLabel.Text = "100 %"; }
                _closeButton.Enabled = true;
                _closeButton.Focus();
            }
            catch { }
        }

        private static string FormatSize(long bytes)
        {
            if (bytes <= 0) return "0 B";
            string[] u = { "B", "KB", "MB", "GB" };
            double v = bytes; int i = 0;
            while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
            return v.ToString("0.##") + " " + u[i];
        }
    }
}
