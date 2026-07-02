using System.Diagnostics;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using WechatDashboard.Application.Mentions;

namespace WechatDashboard.App
{
    /// <summary>
    /// 支持高亮显示 @提及 和链接的自定义 TextBlock 控件
    /// </summary>
    public class HighlightTextBlock : System.Windows.Controls.TextBlock
    {
        /// <summary>
        /// ContentSegments 依赖属性
        /// </summary>
        public static readonly DependencyProperty ContentSegmentsProperty =
            DependencyProperty.Register(
                nameof(ContentSegments),
                typeof(IEnumerable<MessageHighlighter.TextSegment>),
                typeof(HighlightTextBlock),
                new PropertyMetadata(null, OnContentSegmentsChanged));

        /// <summary>
        /// 获取或设置文本片段集合
        /// </summary>
        public IEnumerable<MessageHighlighter.TextSegment>? ContentSegments
        {
            get => (IEnumerable<MessageHighlighter.TextSegment>?)GetValue(ContentSegmentsProperty);
            set => SetValue(ContentSegmentsProperty, value);
        }

        private static void OnContentSegmentsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is HighlightTextBlock textBlock && e.NewValue is IEnumerable<MessageHighlighter.TextSegment> segments)
            {
                textBlock.Inlines.Clear();
                foreach (var segment in segments)
                {
                    switch (segment.Type)
                    {
                        case MessageHighlighter.SegmentType.Link when !string.IsNullOrEmpty(segment.LinkUrl):
                            var hyperlink = new Hyperlink(new Run(segment.Text))
                            {
                                Foreground = new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB)),
                                Cursor = System.Windows.Input.Cursors.Hand
                            };
                            var url = segment.LinkUrl;
                            hyperlink.Click += (_, _) =>
                            {
                                try
                                {
                                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                                }
                                catch
                                {
                                }
                            };
                            textBlock.Inlines.Add(hyperlink);
                            break;

                        case MessageHighlighter.SegmentType.Mention:
                            var mentionRun = new Run(segment.Text)
                            {
                                Foreground = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)),
                                FontWeight = FontWeights.Bold,
                                Background = new SolidColorBrush(Color.FromRgb(0xFE, 0xF2, 0xF2))
                            };
                            textBlock.Inlines.Add(mentionRun);
                            break;

                        default:
                            textBlock.Inlines.Add(new Run(segment.Text));
                            break;
                    }
                }
            }
        }
    }
}
