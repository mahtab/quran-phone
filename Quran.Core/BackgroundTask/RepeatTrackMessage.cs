using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using System.Threading.Tasks;

namespace Quran.Core
{
    [DataContract]
    public class RepeatTrackMessage
    {
        [DataMember]
        public int RepeatCount { get; set; }

        public RepeatTrackMessage(int repeatCount)
        {
            RepeatCount = repeatCount;
        }
    }
}
