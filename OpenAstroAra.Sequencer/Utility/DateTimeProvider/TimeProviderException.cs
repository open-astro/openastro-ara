using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OpenAstroAra.Sequencer.Utility.DateTimeProvider {
    public class TimeProviderException : Exception {
        public TimeProviderException(string message, string localizedMessage) : base(message) {
            LocalizedMessage = localizedMessage;
        }

        public string LocalizedMessage { get; } = string.Empty;

        public TimeProviderException() {
        }

        public TimeProviderException(string message) : base(message) {
        }

        public TimeProviderException(string message, Exception innerException) : base(message, innerException) {
        }
    }
}