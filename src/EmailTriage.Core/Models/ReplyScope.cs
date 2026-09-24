namespace EmailTriage.Core.Models;

public enum ReplyScope
{
    /// <summary>Reply to sender plus every other recipient (bound to `r`).</summary>
    All,

    /// <summary>Reply to the sender alone (bound to Shift+R).</summary>
    SenderOnly,
}
