namespace YoutubeSubscription.Services;

/// <summary>
/// YouTube Data API daily quota resets at midnight Pacific Time.
/// Converts that instant to Taipei so the UI can label it locally.
/// </summary>
public static class YouTubeQuotaReset
{
    private static readonly TimeZoneInfo Pacific = Resolve(
        "Pacific Standard Time",
        "America/Los_Angeles");

    private static readonly TimeZoneInfo Taipei = Resolve(
        "Taipei Standard Time",
        "Asia/Taipei",
        fallbackOffsetHours: 8);

    public static string TaipeiAnnotation(DateTimeOffset? utcNow = null)
    {
        var nowUtc = ToUtc(utcNow ?? DateTimeOffset.UtcNow);
        var resetTaipei = NextResetInTaipei(nowUtc);
        var taipeiNow = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, Taipei);
        var day = resetTaipei.Date == taipeiNow.Date
            ? "今天"
            : resetTaipei.Date == taipeiNow.Date.AddDays(1)
                ? "明天"
                : $"{resetTaipei.Month}月{resetTaipei.Day}日";
        return $"太平洋時間午夜，對應台北時間{day} {resetTaipei:HH:mm}";
    }

    internal static DateTime NextResetInTaipei(DateTime utcNow)
    {
        utcNow = ToUtc(utcNow);
        var pacificNow = TimeZoneInfo.ConvertTimeFromUtc(utcNow, Pacific);
        var nextMidnightPacific = pacificNow.Date;
        if (pacificNow > nextMidnightPacific)
            nextMidnightPacific = nextMidnightPacific.AddDays(1);

        var resetUtc = TimeZoneInfo.ConvertTimeToUtc(nextMidnightPacific, Pacific);
        return TimeZoneInfo.ConvertTimeFromUtc(resetUtc, Taipei);
    }

    private static DateTime ToUtc(DateTimeOffset value) => value.UtcDateTime;

    private static DateTime ToUtc(DateTime value) =>
        value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };

    private static TimeZoneInfo Resolve(string windowsId, string ianaId, int? fallbackOffsetHours = null)
    {
        foreach (var id in new[] { windowsId, ianaId })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }

        if (fallbackOffsetHours is int hours)
        {
            return TimeZoneInfo.CreateCustomTimeZone(
                ianaId,
                TimeSpan.FromHours(hours),
                ianaId,
                ianaId);
        }

        throw new TimeZoneNotFoundException($"找不到時區：{windowsId} / {ianaId}");
    }
}
