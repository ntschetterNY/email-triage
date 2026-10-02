namespace EmailTriage.Core.Models;

/// <summary>What about a mail is remembered when it is filed.</summary>
public enum FilingFeatureKind
{
    /// <summary>The sender's address, lowercased.</summary>
    Sender = 0,

    /// <summary>The part of the sender's address after the @.</summary>
    Domain = 1,

    /// <summary>One word of the subject, prefixes and tags stripped.</summary>
    SubjectWord = 2,
}

/// <summary>One thing noticed about a mail: who sent it, or a word in its subject.</summary>
public readonly record struct FilingFeature(FilingFeatureKind Kind, string Token);

/// <summary>
/// How many mails with <see cref="Token"/> have been seen in a folder, from
/// moves made in the app and from a quiet sample of what is already filed.
/// </summary>
public sealed record FilingEvidence(string FolderPath, FilingFeatureKind Kind, string Token, long Count);
