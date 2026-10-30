using System;

namespace Urbe.Sim.Vehicles
{
    public static class VehicleTypeId
    {
        public const byte Car = 0;
        public const byte Truck = 1;
    }

    /// <summary>Parametros IDM de un tipo de vehiculo (ADR 002). Unidades: m, s, m/s, m/s2.</summary>
    public sealed class VehicleType
    {
        public readonly float V0Factor;
        public readonly float VMax;
        public readonly float AMax;
        /// <summary>Deceleracion comoda.</summary>
        public readonly float B;
        /// <summary>Intervalo temporal deseado.</summary>
        public readonly float T;
        public readonly float S0;
        public readonly float Length;
        /// <summary>1 / (2 * sqrt(AMax * B)), precalculado.</summary>
        public readonly float InvTwoSqrtAB;

        public VehicleType(float v0Factor, float vMax, float aMax, float b, float t, float s0, float length)
        {
            if (!(aMax > 0f) || !(b > 0f) || !(vMax > 0f) || !(length > 0f))
                throw new ArgumentException("Parametros IDM invalidos");
            V0Factor = v0Factor;
            VMax = vMax;
            AMax = aMax;
            B = b;
            T = t;
            S0 = s0;
            Length = length;
            InvTwoSqrtAB = 1f / (2f * (float)Math.Sqrt(aMax * b));
        }

        /// <summary>Espacio que un vehiculo reserva/necesita en un carril de salida: longitud + s0.</summary>
        public float Space => Length + S0;

        /// <summary>Tabla del ADR 002: indice 0 = coche, 1 = camion.</summary>
        public static VehicleType[] CreateDefaults()
        {
            return new[]
            {
                new VehicleType(1.0f, 36f, 1.5f, 2.0f, 1.2f, 2.0f, 4.5f),
                new VehicleType(0.9f, 25f, 0.8f, 1.5f, 1.8f, 3.0f, 10.0f),
            };
        }
    }

    /// <summary>Constantes comunes del movimiento (ADR 002).</summary>
    public sealed class VehicleConfig
    {
        public float Dt = 0.1f;
        public float BEmergency = 9f;
        public float MinGap = 0.5f;
        public float StopMargin = 1f;
        public float LookaheadMax = 120f;
        /// <summary>Margen extra de la distancia a la que se pide permiso de entrada.</summary>
        public float RequestExtra = 5f;
        /// <summary>El vehiculo llega cuando S >= TargetS - ArrivalTolerance.</summary>
        public float ArrivalTolerance = 0.5f;
        /// <summary>Factor del conductor aleatorio en [0,9, 1,1]; si es false vale 1,0.</summary>
        public bool DriverVariation = true;

        // ---- Cambio de carril (M1-08) ----
        /// <summary>false desactiva el cambio de carril (los vehiculos en un carril que no conecta se quedan en el).</summary>
        public bool LaneChangeEnabled = true;
        /// <summary>Duracion de la maniobra lateral (ticks): el render interpola el desplazamiento y el carril de origen conserva un fantasma.</summary>
        public int LaneChangeTicks = 25;
        /// <summary>Ticks de espera tras una maniobra antes de iniciar otra.</summary>
        public int LaneChangeCooldownTicks = 10;
        /// <summary>Frenada maxima (m/s2) que un cambio puede imponer al nuevo seguidor o al propio vehiculo.</summary>
        public float BSafe = 3f;
        /// <summary>Con urgencia 1 (al final del carril) se tolera que el nuevo seguidor frene hasta BSafe + esto (los demas ceden).</summary>
        public float LaneChangeUrgentExtraBrake = 2f;
        /// <summary>Hueco extra (m, se anula con urgencia 1) respecto al espacio reservado al inicio del carril para cambiar de carril con alguien entrando.</summary>
        public float LaneChangeEntryClearance = 15f;
        /// <summary>Distancia (m) que se considera necesaria por cada cambio de carril al calcular la urgencia.</summary>
        public float LaneChangeNominalDistance = 60f;
        /// <summary>Ticks parado en el carril equivocado tras los que se relaja el plan de carriles (salir desde el carril actual).</summary>
        public int LaneChangeRelaxWaitTicks = 150;
        /// <summary>Ticks parado en el carril equivocado tras los que se pide re-ruta (ultimo recurso).</summary>
        public int LaneChangeRerouteWaitTicks = 600;
    }
}
