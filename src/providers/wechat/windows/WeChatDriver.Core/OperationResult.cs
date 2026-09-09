namespace WeChatDriver.Core;

/// <summary>统一操作结果。成功时携带值，失败时携带结构化失败码。</summary>
public sealed record OperationResult<T>
{
    public bool Succeeded { get; init; }
    public T? Value { get; init; }
    public FailureInfo? Failure { get; init; }
    public double Confidence { get; init; }
    public TimeSpan Elapsed { get; init; }

    public static OperationResult<T> Ok(T value, double confidence = 1.0, TimeSpan? elapsed = null) =>
        new() { Succeeded = true, Value = value, Confidence = confidence, Elapsed = elapsed ?? TimeSpan.Zero };

    public static OperationResult<T> Fail(FailureCode code, string message, string? operationId = null, double? confidence = null) =>
        new()
        {
            Succeeded = false,
            Failure = new FailureInfo(code, message, DateTimeOffset.Now, operationId, confidence),
            Confidence = confidence ?? 0.0
        };

    public static OperationResult<T> FromFailure(FailureInfo failure) =>
        new() { Succeeded = false, Failure = failure, Confidence = failure.Confidence ?? 0.0 };
}

/// <summary>无值操作结果。</summary>
public sealed record OperationResult
{
    public bool Succeeded { get; init; }
    public FailureInfo? Failure { get; init; }
    public double Confidence { get; init; }
    public TimeSpan Elapsed { get; init; }

    public static OperationResult Ok(double confidence = 1.0, TimeSpan? elapsed = null) =>
        new() { Succeeded = true, Confidence = confidence, Elapsed = elapsed ?? TimeSpan.Zero };

    public static OperationResult Fail(FailureCode code, string message, string? operationId = null, double? confidence = null) =>
        new()
        {
            Succeeded = false,
            Failure = new FailureInfo(code, message, DateTimeOffset.Now, operationId, confidence),
            Confidence = confidence ?? 0.0
        };
}
