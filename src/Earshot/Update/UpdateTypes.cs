namespace Earshot.Update;

// Why a check or a download stopped. Each kind has a plain sentence for the person (UpdateFailure.Reason) and the
// raw cause for the log (UpdateFailure.Detail): the HTTP status, or the exception type and its code.
internal enum UpdateFailureKind
{
    Network,
    TimedOut,
    HttpStatus,
    BadResponse,
    NotHttps,
    NoZipAsset,
    NoChecksumAsset,
    ChecksumMalformed,
    ChecksumMismatch,
    Truncated,
    TooLarge,
    BadArchive,
    StagingFailed,
    Cancelled,
}

internal sealed record UpdateFailure(UpdateFailureKind Kind, string Reason, string Detail);

// A release newer than the running program, with the two files it publishes for it: the zip, and the file that
// holds the zip's SHA-256.
internal sealed record ReleaseInfo(ReleaseVersion Version, string Tag, string ZipName, Uri ZipUri, string ChecksumName, Uri ChecksumUri, long? ZipSize);

internal enum UpdateCheckOutcome
{
    UpToDate,
    Available,
    Failed,
}

// Latest is the version the feed named, whenever it could be read; Release is set only for Available.
internal sealed record UpdateCheckResult(UpdateCheckOutcome Outcome, ReleaseVersion? Latest, ReleaseInfo? Release, UpdateFailure? Failure)
{
    public static UpdateCheckResult UpToDate(ReleaseVersion latest) => new(UpdateCheckOutcome.UpToDate, latest, null, null);

    public static UpdateCheckResult Available(ReleaseInfo release) => new(UpdateCheckOutcome.Available, release.Version, release, null);

    public static UpdateCheckResult Failed(UpdateFailure failure) => new(UpdateCheckOutcome.Failed, null, null, failure);
}

// Total is null when the server did not say how big the file is.
internal readonly record struct UpdateProgress(long Received, long? Total)
{
    // 0 to 100, or null while the size is not known.
    public int? Percent => Total is > 0 ? (int)Math.Min(100, Received * 100 / Total.Value) : null;
}

// A verified download (the zip, and the hash it matched), or the reason there is none. Exactly one of the two is set.
internal sealed record UpdateDownloadResult(StagedUpdate? Staged, UpdateFailure? Failure)
{
    public static UpdateDownloadResult Success(StagedUpdate staged) => new(staged, null);

    public static UpdateDownloadResult Failed(UpdateFailure failure) => new(null, failure);
}

// What the update controller needs from the network side. The real one is UpdateService; tests supply their own
// to drive the controller through each state.
internal interface IUpdateSource
{
    Task<UpdateCheckResult> CheckAsync(CancellationToken ct);

    Task<UpdateDownloadResult> DownloadAsync(ReleaseInfo release, IProgress<UpdateProgress>? progress, CancellationToken ct);
}
