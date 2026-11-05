using System;

namespace Urbe.Sim.City
{
    /// <summary>
    /// Calendario de la ciudad (ADR 005): 1 dia = 3.600 ticks (6 min a x1, 10 ticks por segundo), 1 semana = 7 dias, 1 ano = 52 semanas.
    /// El tick 0 es el dia 0 a las 06:00. Se calcula a partir del tick, asi que no tiene estado propio que hashear.
    /// </summary>
    public readonly struct CityClock
    {
        public const int TicksPerDay = 3600;
        public const int TicksPerHour = TicksPerDay / 24;
        public const int DaysPerWeek = 7;
        public const int WeeksPerYear = 52;
        public const int StartHour = 6;

        public readonly ulong Tick;

        public CityClock(ulong tick) { Tick = tick; }

        private ulong Shifted => Tick + (ulong)(StartHour * TicksPerHour);
        public int Day => (int)(Shifted / TicksPerDay);
        public int Week => Day / DaysPerWeek;
        public int Year => Week / WeeksPerYear;
        public int DayOfWeek => Day % DaysPerWeek;
        /// <summary>Sabado y domingo.</summary>
        public bool IsWeekend => DayOfWeek >= 5;
        public int TickOfDay => (int)(Shifted % TicksPerDay);
        /// <summary>Hora del dia con decimales (0 a 24).</summary>
        public float Hour => TickOfDay * (24f / TicksPerDay);

        public static float HourOf(ulong tick) => new CityClock(tick).Hour;
    }
}
