using System;

namespace ClashWinUI.Models
{
    public sealed class SubscriptionDownloadFailedException : InvalidOperationException
    {
        public SubscriptionDownloadFailedException(string message)
            : base(message)
        {
        }

        public SubscriptionDownloadFailedException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
