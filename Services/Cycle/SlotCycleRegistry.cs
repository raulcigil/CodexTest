using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TestPlan.Logic.Services.Cycle
{
    public static class SlotCycleRegistry
    {
        private static readonly Dictionary<int, CycleService> _slotCycles = new();

        public static void Register(int slotId, CycleService cycle)
        {
            _slotCycles[slotId] = cycle;
        }

        public static void Unregister(int slotId)
        {
            _slotCycles.Remove(slotId);
        }

        public static void CancelSlotCycle(int slotId)
        {
            if (_slotCycles.TryGetValue(slotId, out var cycle))
            {
                cycle.CancelCurrentOperation();
            }
        }

        public static CycleService? Get(int slotId)
        {
            _slotCycles.TryGetValue(slotId, out var cycle);
            return cycle;
        }
    }

}
