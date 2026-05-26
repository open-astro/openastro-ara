using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OpenAstroAra.Sequencer.Utility {
    public class SequenceEntityFailureEventArgs {
        public SequenceEntityFailureEventArgs(ISequenceEntity entity, Exception ex) {
            this.Entity = entity;
            this.Exception = ex;
        }

        public ISequenceEntity Entity { get; }

        public Exception Exception { get; }
    }
}
