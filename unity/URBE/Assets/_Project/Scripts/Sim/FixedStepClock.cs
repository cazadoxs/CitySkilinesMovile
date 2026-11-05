using System;

namespace Urbe.Game.Sim
{
    /// <summary>
    /// Acumulador de paso fijo (puro, sin Unity, testeable en EditMode). Convierte el tiempo real de cada fotograma en un número
    /// entero de ticks y expone el resto como <see cref="Alpha"/> para interpolar el render.
    /// </summary>
    public sealed class FixedStepClock
    {
        public float TickDt { get; }
        public int MaxTicksPerFrame { get; }

        private float _accumulator;

        /// <summary>Ticks descartados por no poder alcanzar el tiempo real (fotogramas largos o ×3 en un móvil lento).</summary>
        public long DroppedTicks { get; private set; }

        public FixedStepClock(float tickDt = 0.1f, int maxTicksPerFrame = 8)
        {
            if (tickDt <= 0f) throw new ArgumentOutOfRangeException(nameof(tickDt));
            if (maxTicksPerFrame < 1) throw new ArgumentOutOfRangeException(nameof(maxTicksPerFrame));
            TickDt = tickDt;
            MaxTicksPerFrame = maxTicksPerFrame;
        }

        /// <summary>Fracción del tick en curso, en [0, 1). Con pausa se congela.</summary>
        public float Alpha => _accumulator / TickDt;

        /// <summary>
        /// Suma <paramref name="realDt"/> × <paramref name="speed"/> y devuelve cuántos ticks hay que ejecutar ahora. Con
        /// <paramref name="speed"/> = 0 (pausa) devuelve 0 y no toca el acumulador.
        /// </summary>
        public int Advance(float realDt, float speed)
        {
            if (speed <= 0f || realDt <= 0f) return 0;
            _accumulator += realDt * speed;
            int ticks = (int)(_accumulator / TickDt);
            _accumulator -= ticks * TickDt;
            if (ticks > MaxTicksPerFrame)
            {
                DroppedTicks += ticks - MaxTicksPerFrame;
                ticks = MaxTicksPerFrame;
            }
            if (_accumulator < 0f) _accumulator = 0f;
            return ticks;
        }

        public void Reset()
        {
            _accumulator = 0f;
            DroppedTicks = 0;
        }
    }
}
