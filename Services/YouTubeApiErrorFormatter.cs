using Google;

namespace YoutubeSubscription.Services;

/// <summary>Converts YouTube Data API failures into short, actionable UI text.</summary>
public static class YouTubeApiErrorFormatter
{
    public static string ForLoading(Exception exception) => GetMessage(exception, "載入訂閱");

    public static string ForUnsubscribe(Exception exception) => GetMessage(exception, "取消訂閱");

    private static string GetMessage(Exception exception, string operation)
    {
        if (exception is not GoogleApiException apiException)
            return $"{operation}失敗，請確認網路連線後再試。";

        var reason = apiException.Error?.Errors?.FirstOrDefault()?.Reason;
        return reason switch
        {
            "quotaExceeded" or "dailyLimitExceeded" =>
                $"{operation}失敗：YouTube API 配額目前受限。請明天再試（配額於太平洋時間午夜重置），或檢查 Google Cloud 的配額。",
            "rateLimitExceeded" or "userRateLimitExceeded" =>
                $"{operation}失敗：請求過於頻繁。請稍候一分鐘後再試。",
            "subscriptionForbidden" =>
                $"{operation}失敗：目前帳號沒有存取訂閱清單的權限。請重新登入並授權。",
            "accountClosed" or "accountSuspended" =>
                $"{operation}失敗：此 YouTube 帳號目前無法使用。請確認帳號狀態。",
            _ => $"{operation}失敗（HTTP {(int?)apiException.HttpStatusCode ?? 0}）。請稍後再試。",
        };
    }
}
