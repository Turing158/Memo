using System;

namespace Memo.Utils;

/// <summary>
/// 日期时间工具类：统一 wall-clock 获取、本地转换与常用格式化，避免 DateTime 散落在各处。
/// </summary>
public static class DateTimeUtils {
    /// <summary>当前本地时间（wall-clock）。</summary>
    public static DateTime Now => DateTime.Now;

    /// <summary>转换为本地时间。</summary>
    public static DateTime ToLocal(this DateTime time) => time.ToLocalTime();

    /// <summary>固定格式的全时间：yyyy-MM-dd HH:mm（本地时间）。</summary>
    public static string ToFullTimeString(this DateTime time) =>
        time.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    /// <summary>短时间：HH:mm（本地时间）。</summary>
    public static string ToShortTimeString(this DateTime time) =>
        time.ToLocalTime().ToString("HH:mm");

    /// <summary>相对时间：今天显示 HH:mm，昨天显示"昨天 HH:mm"，一周内显示"周x HH:mm"，同年显示 MM-dd HH:mm，跨年显示 yyyy-MM-dd HH:mm。</summary>
    public static string ToRelativeTimeString(this DateTime time) {
        var local = time.ToLocalTime();
        var now = DateTime.Now;
        var diff = now.Date - local.Date;

        var timeStr = local.ToString("HH:mm");

        if (diff.TotalDays < 1)
            return timeStr;
        if (diff.TotalDays < 2)
            return "昨天 " + timeStr;
        if (diff.TotalDays < 8) {
            var dayOfWeek = local.DayOfWeek switch {
                DayOfWeek.Monday => "周一",
                DayOfWeek.Tuesday => "周二",
                DayOfWeek.Wednesday => "周三",
                DayOfWeek.Thursday => "周四",
                DayOfWeek.Friday => "周五",
                DayOfWeek.Saturday => "周六",
                DayOfWeek.Sunday => "周日",
                _ => ""
            };
            return $"{dayOfWeek} {timeStr}";
        }
        if (local.Year == now.Year)
            return local.ToString("MM-dd") + " " + timeStr;
        return local.ToString("yyyy-MM-dd") + " " + timeStr;
    }
}
