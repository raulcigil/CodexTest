using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TestPlan.Logic.Services.Alarms
{
    public class SlotSafetyStatus
    {
        public int SlotId { get; set; }
        public bool HasEmergency { get; set; }
        public bool IsSafe => !HasEmergency;
    }
}
