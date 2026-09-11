using System;
using System.Globalization;
using System.Windows.Data;

namespace XAssistant.Converters
{
    /// <summary>
    /// 把计数值压成定宽的量级缩写：一万及以上写成 12.3k / 1.2M / 1.2B，一万以下保留千分位的精确值。
    /// 只用在宽度固定、字号又大的数字位（顶部统计卡的累计值）；
    /// 表格和小字说明里要读准确数，不适用这个转换器。
    /// </summary>
    public class CompactCountConverter : IValueConverter
    {
        /// <summary>起缩档位。「1,234」六个字符在本就不窄，缩成「1.2k」只是丢精度，所以从万位开始。</summary>
        private const int CompactFrom = 10_000;

        private static readonly string[] Units = ["k", "M", "B", "T"];

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var count = value switch
            {
                int i => (long)i,
                long l => l,
                _ => 0L,
            };
            if (count < CompactFrom)
                return count.ToString("N0", culture);
            return Abbreviate(count, culture);
        }

        /// <summary>只作单向显示用；绑定目标都是 TextBlock.Text。</summary>
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value;

        private static string Abbreviate(long count, CultureInfo culture)
        {
            // 逐档上抬并四舍五入到一位小数。进档判断放在舍入之后：
            // 999950 这类数在 k 档舍入后正好满千（1000k），继续上抬才得到 1M
            var scaled = (double)count;
            for (var tier = 0; ; tier++)
            {
                var isLastTier = tier == Units.Length - 1;
                scaled /= 1000;
                var rounded = Math.Round(scaled, 1);
                if (rounded < 1000 || isLastTier)
                    return rounded.ToString("0.#", culture) + Units[tier];
                scaled = rounded;
            }
        }
    }
}
