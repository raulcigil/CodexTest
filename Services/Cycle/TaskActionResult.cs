using TestPlan.Entities.Enumeraciones;

namespace TestPlan.Logic.Services.Cycle
{
    public struct TaskActionResult
    {
        public eResult Result { get; set; }
        public int CurrentIndex { get; set; }
        public int NewIndex { get; set; }

        /// <summary>
        /// ToString override to return the result as a string representation.
        /// </summary>
        /// <returns></returns>
        public override string ToString()
        {
            return Result.ToString();
        }
    }
}
