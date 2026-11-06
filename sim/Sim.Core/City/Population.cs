using System;
using Urbe.Sim.Geometry;
using Urbe.Sim.Ids;
using Urbe.Sim.Random;

namespace Urbe.Sim.City
{
    public enum CitizenState : byte { AtHome = 0, ToWork = 1, AtWork = 2, ToShop = 3, AtShop = 4, ToHome = 5 }

    [Flags]
    public enum CitizenFlags : byte
    {
        None = 0,
        /// <summary>Tiene entre 6 y 17 anos y busca colegio.</summary>
        Student = 1,
        /// <summary>Su empleo es de alta cualificacion.</summary>
        JobHigh = 2,
        /// <summary>Ya no existe pero sigue en un viaje en curso: el hueco se libera cuando el viaje acaba.</summary>
        Zombie = 4,
        AttendedSchool = 8,
        AttendedHighSchool = 16,
    }

    /// <summary>Ciudadano (GDD 8.3): una fila en un array; solo existe fisicamente mientras su viaje es un vehiculo.</summary>
    public struct Citizen
    {
        /// <summary>Hogar; -1 = hueco libre.</summary>
        public int Household;
        /// <summary>Trabajo o colegio (EntityId de edificio); None = ninguno.</summary>
        public EntityId Workplace;
        /// <summary>Edificio donde esta (casa, trabajo, tienda) cuando no viaja.</summary>
        public int Location;
        /// <summary>Destino del viaje en curso.</summary>
        public int Dest;
        public ulong StateSince;
        public int NextInHousehold;
        /// <summary>Dia del calendario del ultimo viaje al trabajo o a la tienda (para salir una vez por dia).</summary>
        public int LastTripDay;
        public byte Age;
        /// <summary>0 a 3.</summary>
        public byte Education;
        public byte Health;
        public byte Happiness;
        public CitizenState State;
        public CitizenFlags Flags;
        /// <summary>Hora de salida al trabajo en decimas de hora sobre las 6:00 (0 a 35).</summary>
        public byte StartOffset;
        public byte UnemployedDays;

        public bool IsAlive => Household >= 0;
    }

    public struct Household
    {
        public EntityId Home;
        public int FirstMember;
        public byte Members;
        public byte Cars;
        public byte UnhappyDays;
        public bool IsAlive;
    }

    /// <summary>
    /// Poblacion virtual (GDD 4.11 y 8.3): ciudadanos y hogares en arrays, migracion, ciclo vital, empleo, educacion y felicidad.
    /// Los viajes de la rutina diaria los gestiona <see cref="TripPlanner"/>.
    /// </summary>
    public sealed class PopulationSystem
    {
        private readonly CitySimulation _city;
        private readonly CityBalance _bal;
        private Pcg32 _rng;

        private Citizen[] _c = new Citizen[1024];
        private int _cHigh;
        private int[] _cFree = new int[256];
        private int _cFreeCount;
        private int _alive;

        private Household[] _h = new Household[512];
        private int _hHigh;
        private int[] _hFree = new int[128];
        private int _hFreeCount;
        private int _households;

        private float _immigrationAcc;
        private int _employmentPhase;

        // Listas de edificios, reconstruidas cuando cambia el conjunto de edificios.
        private int[] _resList = new int[64], _jobList = new int[64], _comList = new int[64], _schoolList = new int[16];
        private int _resCount, _jobCount, _comCount, _schoolCount;
        private uint _listVersion = uint.MaxValue;

        public long Born { get; private set; }
        public long Died { get; private set; }
        public long Immigrated { get; private set; }
        public long Emigrated { get; private set; }

        public int Count => _alive;
        public int HouseholdCount => _households;
        public int CitizenIndexBound => _cHigh;
        public bool IsAlive(int i) => i >= 0 && i < _cHigh && _c[i].Household >= 0 && (_c[i].Flags & CitizenFlags.Zombie) == 0;
        public ref readonly Citizen Get(int i) => ref _c[i];
        internal ref Citizen GetMutable(int i) => ref _c[i];
        public ref readonly Household GetHousehold(int h) => ref _h[h];

        public PopulationSystem(CitySimulation city, ulong seed)
        {
            _city = city;
            _bal = city.Balance;
            _rng = new Pcg32(seed, 0x504F5055UL);
            for (int i = 0; i < _c.Length; i++) _c[i].Household = -1;
        }

        internal static uint Hash(uint a, uint b)
        {
            unchecked
            {
                uint h = a * 0x9E3779B1u ^ (b + 0x7F4A7C15u) * 0x85EBCA6Bu;
                h ^= h >> 15; h *= 0xC2B2AE35u; h ^= h >> 13;
                return h;
            }
        }

        // ------------------------------------------------------------------ listas de edificios

        internal void EnsureLists()
        {
            BuildingSystem bs = _city.Buildings;
            if (_listVersion == bs.Version) return;
            _listVersion = bs.Version;
            _resCount = _jobCount = _comCount = _schoolCount = 0;
            int bound = bs.IndexBound;
            for (int i = 0; i < bound; i++)
            {
                if (!bs.IsAlive(i)) continue;
                ref readonly Building b = ref bs.Get(i);
                if (b.IsAbandoned) continue;
                if (b.Kind == BuildingKind.Service)
                {
                    int cov = _bal.Service((ServiceType)b.ServiceType).Coverage;
                    if (cov == (int)CoverageType.School || cov == (int)CoverageType.HighSchool) Add(ref _schoolList, ref _schoolCount, i);
                    continue;
                }
                if (b.Zone == ZoneType.Residential) Add(ref _resList, ref _resCount, i);
                else
                {
                    Add(ref _jobList, ref _jobCount, i);
                    if (b.Zone == ZoneType.Commercial) Add(ref _comList, ref _comCount, i);
                }
            }
        }

        private static void Add(ref int[] list, ref int count, int v)
        {
            if (count == list.Length) Array.Resize(ref list, count * 2);
            list[count++] = v;
        }

        internal ReadOnlySpan<int> Commercial { get { EnsureLists(); return new ReadOnlySpan<int>(_comList, 0, _comCount); } }

        // ------------------------------------------------------------------ altas y bajas

        private int NewCitizen()
        {
            int i;
            if (_cFreeCount > 0) i = _cFree[--_cFreeCount];
            else
            {
                i = _cHigh++;
                if (i >= _c.Length)
                {
                    int old = _c.Length;
                    Array.Resize(ref _c, old * 2);
                    for (int k = old; k < _c.Length; k++) _c[k].Household = -1;
                }
            }
            _alive++;
            return i;
        }

        private int NewHousehold(EntityId home)
        {
            int h;
            if (_hFreeCount > 0) h = _hFree[--_hFreeCount];
            else
            {
                h = _hHigh++;
                if (h >= _h.Length) Array.Resize(ref _h, _h.Length * 2);
            }
            _h[h] = new Household { Home = home, FirstMember = -1, IsAlive = true };
            _households++;
            return h;
        }

        private int AddCitizen(int household, int age, int education)
        {
            int i = NewCitizen();
            ref Household hh = ref _h[household];
            _c[i] = new Citizen
            {
                Household = household,
                Workplace = EntityId.None,
                Location = hh.Home.Index,
                Dest = -1,
                StateSince = _city.Tick,
                NextInHousehold = hh.FirstMember,
                LastTripDay = -1,
                Age = (byte)age,
                Education = (byte)education,
                Health = 90,
                Happiness = 60,
                State = CitizenState.AtHome,
                StartOffset = (byte)_rng.NextInt(36),
            };
            if (age >= 6 && age < 18) _c[i].Flags |= CitizenFlags.Student;
            hh.FirstMember = i;
            hh.Members++;
            return i;
        }

        /// <summary>Quita a un ciudadano (muerte, emigracion o desahucio). Si esta en un viaje se libera cuando el viaje termine.</summary>
        internal void Remove(int i)
        {
            ref Citizen c = ref _c[i];
            if (c.Household < 0 || (c.Flags & CitizenFlags.Zombie) != 0) return;
            int hi = c.Household;
            ref Household hh = ref _h[hi];
            // Sacarlo de la lista del hogar.
            if (hh.FirstMember == i) hh.FirstMember = c.NextInHousehold;
            else
            {
                for (int m = hh.FirstMember; m >= 0; m = _c[m].NextInHousehold)
                {
                    if (_c[m].NextInHousehold != i) continue;
                    _c[m].NextInHousehold = c.NextInHousehold;
                    break;
                }
            }
            hh.Members--;
            if (_city.Buildings.IsAlive(hh.Home) && _city.Buildings.Get(hh.Home).Occupants > 0) _city.Buildings.GetMutable(hh.Home.Index).Occupants--;
            if (hh.Members == 0)
            {
                hh.IsAlive = false;
                if (_hFreeCount == _hFree.Length) Array.Resize(ref _hFree, _hFreeCount * 2);
                _hFree[_hFreeCount++] = hi;
                _households--;
            }
            _alive--;
            bool traveling = c.State == CitizenState.ToWork || c.State == CitizenState.ToShop || c.State == CitizenState.ToHome;
            if (traveling && _city.Trips.IsInFlight(i))
            {
                c.Flags |= CitizenFlags.Zombie;
                c.Workplace = EntityId.None;
                return;
            }
            Release(i);
        }

        /// <summary>Libera el hueco de un ciudadano ya retirado (o zombi cuyo viaje termino).</summary>
        internal void Release(int i)
        {
            _c[i] = default;
            _c[i].Household = -1;
            if (_cFreeCount == _cFree.Length) Array.Resize(ref _cFree, _cFreeCount * 2);
            _cFree[_cFreeCount++] = i;
        }

        private void RemoveHousehold(int hi)
        {
            while (_h[hi].IsAlive && _h[hi].FirstMember >= 0) Remove(_h[hi].FirstMember);
        }

        // ------------------------------------------------------------------ tick

        public void Step(ulong tick)
        {
            EnsureLists();
            if (tick % (ulong)_bal.ImmigrationInterval == 0) Immigrate();
            if (tick % (ulong)_bal.EmploymentInterval == 0) EmploymentSlice();
            DailySlice(tick);
        }

        // ------------------------------------------------------------------ inmigracion

        private void Immigrate()
        {
            if (_alive >= _bal.MaxPopulation) return;
            CityStats s = _city.Stats;
            if (_alive > 0 && s.HousingVacant <= 0) return;
            float attract = _alive == 0 ? 20f : s.JobsFree - 0.5f * s.Unemployed + 25f;
            if (attract <= 0f) return;
            float hf = s.AverageHappiness / 60f;
            hf = hf < 0.2f ? 0.2f : (hf > 1.2f ? 1.2f : hf);
            float rate = Math.Min(_bal.ImmigrationMaxPerBatch, attract / 25f) * hf;
            _immigrationAcc += rate;
            while (_immigrationAcc >= 1f)
            {
                _immigrationAcc -= 1f;
                if (!SpawnHousehold()) { _immigrationAcc = 0f; break; }
            }
        }

        private bool SpawnHousehold()
        {
            int best = -1, bestVac = 0;
            BuildingSystem bs = _city.Buildings;
            int tries = Math.Min(16, _resCount);
            for (int k = 0; k < tries; k++)
            {
                int bi = _resList[_rng.NextInt(_resCount)];
                if (!bs.IsAlive(bi)) continue;
                ref readonly Building b = ref bs.Get(bi);
                int vac = b.Capacity - b.Occupants;
                if (vac <= bestVac || b.IsAbandoned || !_city.CanGrowAt(b.Segment)) continue;
                best = bi;
                bestVac = vac;
            }
            if (best < 0) return false;
            int size = Math.Min(bestVac, 1 + _rng.NextInt(_bal.ImmigrantHouseholdMax));
            int hi = NewHousehold(bs.IdAt(best));
            int adults = size >= 3 ? 2 : size;
            for (int m = 0; m < size; m++)
            {
                bool child = m >= adults;
                int age = child ? 1 + _rng.NextInt(15) : 20 + _rng.NextInt(30);
                int edu = child ? 0 : (_rng.NextFloat() < 0.15f ? 2 : (_rng.NextFloat() < 0.4f ? 1 : 0));
                AddCitizen(hi, age, edu);
            }
            _h[hi].Cars = (byte)((_rng.NextFloat() < _bal.CarOwnership ? 1 : 0) + (size >= 3 && _rng.NextFloat() < 0.3f ? 1 : 0));
            bs.GetMutable(best).Occupants += (ushort)size;
            Immigrated += size;
            return true;
        }

        // ------------------------------------------------------------------ empleo y colegio

        private void EmploymentSlice()
        {
            const int Slices = 16;
            int phase = _employmentPhase++ % Slices;
            BuildingSystem bs = _city.Buildings;
            for (int i = phase; i < _cHigh; i += Slices)
            {
                ref Citizen c = ref _c[i];
                if (c.Household < 0 || (c.Flags & CitizenFlags.Zombie) != 0) continue;
                if (c.Workplace.IsValid && !(bs.IsAlive(c.Workplace) && !bs.Get(c.Workplace).IsAbandoned))
                {
                    c.Workplace = EntityId.None;
                    c.Flags &= ~CitizenFlags.JobHigh;
                }
                if (c.Workplace.IsValid) continue;
                if ((c.Flags & CitizenFlags.Student) != 0) FindSchool(i);
                else if (c.Age >= 18 && c.Age < 65) FindJob(i);
            }
        }

        private Vec2 HomePos(in Citizen c) => _city.Buildings.IsAlive(_h[c.Household].Home) ? _city.Buildings.Get(_h[c.Household].Home).Pos : Vec2.Zero;

        private void FindJob(int ci)
        {
            ref Citizen c = ref _c[ci];
            BuildingSystem bs = _city.Buildings;
            ref readonly Household hh = ref _h[c.Household];
            if (!bs.IsAlive(hh.Home) || _jobCount == 0) return;
            ref readonly Building home = ref bs.Get(hh.Home);
            int homeComp = _city.Supply.ComponentOf(home.Segment);
            bool skilled = c.Education >= 2;
            int best = -1;
            bool bestHigh = false;
            float bestScore = float.MaxValue;
            int tries = Math.Min(24, _jobCount);
            for (int k = 0; k < tries; k++)
            {
                int bi = _jobList[_rng.NextInt(_jobCount)];
                if (!bs.IsAlive(bi)) continue;
                ref readonly Building b = ref bs.Get(bi);
                if (b.IsAbandoned) continue;
                bool lowFree = b.FilledLow < b.JobsLow, highFree = b.FilledHigh < b.JobsHigh;
                bool takeHigh = skilled && highFree;
                if (!takeHigh && !lowFree) continue;
                if (homeComp < 0 || _city.Supply.ComponentOf(b.Segment) != homeComp) continue;
                float score = Vec2.Distance(home.Pos, b.Pos) - (takeHigh ? 150f : 0f);
                if (score < bestScore) { bestScore = score; best = bi; bestHigh = takeHigh; }
            }
            if (best < 0) return;
            ref Building job = ref bs.GetMutable(best);
            if (bestHigh) { job.FilledHigh++; c.Flags |= CitizenFlags.JobHigh; }
            else { job.FilledLow++; c.Flags &= ~CitizenFlags.JobHigh; }
            job.Occupants++;
            c.Workplace = job.Id;
            c.UnemployedDays = 0;
        }

        private void FindSchool(int ci)
        {
            ref Citizen c = ref _c[ci];
            BuildingSystem bs = _city.Buildings;
            ref readonly Household hh = ref _h[c.Household];
            if (!bs.IsAlive(hh.Home) || _schoolCount == 0) return;
            ref readonly Building home = ref bs.Get(hh.Home);
            int homeComp = _city.Supply.ComponentOf(home.Segment);
            int wantCov = c.Age < 15 ? (int)CoverageType.School : (int)CoverageType.HighSchool;
            int best = -1;
            float bestD = float.MaxValue;
            for (int k = 0; k < _schoolCount; k++)
            {
                int bi = _schoolList[k];
                if (!bs.IsAlive(bi)) continue;
                ref readonly Building b = ref bs.Get(bi);
                ServiceInfo info = _bal.Service((ServiceType)b.ServiceType);
                if (info.Coverage != wantCov || b.Occupants >= info.Students) continue;
                if (_city.Supply.ComponentOf(b.Segment) != homeComp) continue;
                float d = Vec2.Distance(home.Pos, b.Pos);
                if (d < bestD) { bestD = d; best = bi; }
            }
            if (best < 0) return;
            bs.GetMutable(best).Occupants++;
            c.Workplace = bs.IdAt(best);
            c.Flags |= wantCov == (int)CoverageType.School ? CitizenFlags.AttendedSchool : CitizenFlags.AttendedHighSchool;
        }

        // ------------------------------------------------------------------ ciclo diario

        private void DailySlice(ulong tick)
        {
            int tod = new CityClock(tick).TickOfDay;
            int day = new CityClock(tick).Day;
            for (int i = tod; i < _cHigh; i += CityClock.TicksPerDay)
            {
                if (_c[i].Household < 0 || (_c[i].Flags & CitizenFlags.Zombie) != 0) continue;
                DailyCitizen(i, day);
            }
            for (int h = tod; h < _hHigh; h += CityClock.TicksPerDay)
            {
                if (_h[h].IsAlive) DailyHousehold(h, day);
            }
        }

        private void DailyCitizen(int i, int day)
        {
            ref Citizen c = ref _c[i];
            if ((day + i) % _bal.DaysPerAgeYear == 0 && AgeUp(i)) return;
            bool working = c.Age >= 18 && c.Age < 65;
            if (working && !c.Workplace.IsValid) { if (c.UnemployedDays < 250) c.UnemployedDays++; }
            else c.UnemployedDays = 0;
            // Felicidad: objetivo segun empleo, servicios cubiertos, suelo, impuestos y suministro del barrio.
            ref readonly Household hh = ref _h[c.Household];
            float target = 50f;
            if (working) target += c.Workplace.IsValid ? 10f : -18f;
            if (_city.Buildings.IsAlive(hh.Home))
            {
                ref readonly Building home = ref _city.Buildings.Get(hh.Home);
                int mask = _city.CoverageMaskAt(home.Segment);
                if ((mask & (1 << (int)CoverageType.Health)) != 0) target += 6f;
                if ((mask & (1 << (int)CoverageType.Garbage)) != 0) target += 5f;
                if ((mask & (1 << (int)CoverageType.Police)) != 0) target += 4f;
                if ((mask & (1 << (int)CoverageType.Fire)) != 0) target += 3f;
                if ((mask & (1 << (int)CoverageType.Park)) != 0) target += 6f;
                target += (_city.LandValueAt(home.Pos) - 30f) * 0.15f;
                target -= _city.PollutionPenaltyAt(home.Pos);
                if ((home.Problems & (BuildingProblems.NoPower | BuildingProblems.NoWater)) != 0) target -= 25f;
                if ((home.Problems & BuildingProblems.NoSewage) != 0) target -= 6f;
            }
            target -= (_city.TaxRate(ZoneType.Residential) - 9f) * 2.5f;
            if (target < 0f) target = 0f; else if (target > 100f) target = 100f;
            c.Happiness = (byte)(c.Happiness + (target - c.Happiness) * 0.3f + 0.5f);
        }

        /// <summary>Un ano de edad: educacion, jubilacion y muerte. True si el ciudadano murio.</summary>
        private bool AgeUp(int i)
        {
            ref Citizen c = ref _c[i];
            if (c.Age < 120) c.Age++;
            if (c.Age == 6) c.Flags |= CitizenFlags.Student;
            if (c.Age == 15 && (c.Flags & CitizenFlags.AttendedSchool) != 0 && c.Education < 1) c.Education = 1;
            if (c.Age == 18)
            {
                if ((c.Flags & CitizenFlags.AttendedHighSchool) != 0 && c.Education < 2) c.Education = 2;
                c.Flags &= ~(CitizenFlags.Student | CitizenFlags.JobHigh);
                c.Workplace = EntityId.None;
            }
            if (c.Age == 65) { c.Workplace = EntityId.None; c.Flags &= ~CitizenFlags.JobHigh; }
            float p = c.Age < 55 ? 0.0004f : (c.Age < 70 ? 0.004f : (c.Age < 85 ? 0.03f : (c.Age < 100 ? 0.12f : 0.6f)));
            if (c.Health < 40) p *= 2f;
            if (_rng.NextFloat() < p)
            {
                Died++;
                Remove(i);
                return true;
            }
            return false;
        }

        private void DailyHousehold(int hi, int day)
        {
            ref Household hh = ref _h[hi];
            BuildingSystem bs = _city.Buildings;
            // Casa derribada o abandonada: busca otra o se va de la ciudad.
            if (!bs.IsAlive(hh.Home) || bs.Get(hh.Home).IsAbandoned)
            {
                int size = hh.Members;
                int vacancy = -1;
                for (int k = 0; k < Math.Min(12, _resCount); k++)
                {
                    int bi = _resList[_rng.NextInt(_resCount)];
                    if (!bs.IsAlive(bi)) continue;
                    ref readonly Building b = ref bs.Get(bi);
                    if (!b.IsAbandoned && b.Capacity - b.Occupants >= size && _city.CanGrowAt(b.Segment)) { vacancy = bi; break; }
                }
                if (vacancy >= 0)
                {
                    hh.Home = bs.IdAt(vacancy);
                    bs.GetMutable(vacancy).Occupants += hh.Members;
                    for (int m = hh.FirstMember; m >= 0; m = _c[m].NextInHousehold) { _c[m].Location = vacancy; _c[m].State = CitizenState.AtHome; }
                }
                else
                {
                    Emigrated += hh.Members;
                    RemoveHousehold(hi);
                }
                return;
            }
            // Felicidad baja sostenida: emigran.
            int sum = 0, n = 0;
            for (int m = hh.FirstMember; m >= 0; m = _c[m].NextInHousehold) { sum += _c[m].Happiness; n++; }
            if (n > 0 && sum / n < _bal.UnhappyThreshold)
            {
                if (++hh.UnhappyDays >= _bal.UnhappyLeaveDays)
                {
                    Emigrated += hh.Members;
                    RemoveHousehold(hi);
                    return;
                }
            }
            else hh.UnhappyDays = 0;
            // Nacimientos: hogares con dos adultos jovenes y sitio libre en la casa.
            if (hh.Members >= 2 && hh.Members < 6 && _rng.NextFloat() < _bal.BirthChancePerDay)
            {
                int young = 0;
                for (int m = hh.FirstMember; m >= 0; m = _c[m].NextInHousehold) if (_c[m].Age >= 20 && _c[m].Age <= 42) young++;
                ref readonly Building home = ref bs.Get(hh.Home);
                if (young >= 2 && home.Occupants < home.Capacity)
                {
                    AddCitizen(hi, 0, 0);
                    bs.GetMutable(hh.Home.Index).Occupants++;
                    Born++;
                }
            }
        }

        // ------------------------------------------------------------------ estadisticas

        /// <summary>
        /// Rellena los contadores de ocupacion de cada edificio a partir de los ciudadanos (evita derivas): residentes, empleos cubiertos
        /// por tipo, alumnos y educacion media. Llamar antes del barrido de totales de ciudad.
        /// </summary>
        internal void SyncBuildings()
        {
            BuildingSystem bs = _city.Buildings;
            int bound = bs.IndexBound;
            if (_eduSum.Length < bound) _eduSum = new int[Math.Max(bound, _eduSum.Length * 2)];
            if (_eduN.Length < bound) _eduN = new int[Math.Max(bound, _eduN.Length * 2)];
            for (int i = 0; i < bound; i++)
            {
                _eduSum[i] = 0;
                _eduN[i] = 0;
                if (!bs.IsAlive(i)) continue;
                ref Building b = ref bs.GetMutable(i);
                b.Occupants = 0;
                b.FilledLow = 0;
                b.FilledHigh = 0;
            }
            for (int i = 0; i < _cHigh; i++)
            {
                ref Citizen c = ref _c[i];
                if (c.Household < 0 || (c.Flags & CitizenFlags.Zombie) != 0) continue;
                EntityId home = _h[c.Household].Home;
                if (bs.IsAlive(home))
                {
                    bs.GetMutable(home.Index).Occupants++;
                    _eduSum[home.Index] += c.Education;
                    _eduN[home.Index]++;
                }
                if (c.Workplace.IsValid && bs.IsAlive(c.Workplace))
                {
                    ref Building w = ref bs.GetMutable(c.Workplace.Index);
                    w.Occupants++;
                    if (w.Kind == BuildingKind.Zoned)
                    {
                        if ((c.Flags & CitizenFlags.JobHigh) != 0) w.FilledHigh++; else w.FilledLow++;
                    }
                }
            }
            for (int i = 0; i < bound; i++)
            {
                if (!bs.IsAlive(i) || _eduN[i] == 0) continue;
                bs.GetMutable(i).EducationTimes10 = (byte)Math.Min(30, (int)(10f * _eduSum[i] / _eduN[i] + 0.5f));
            }
        }

        private int[] _eduSum = new int[64];
        private int[] _eduN = new int[64];

        internal void FillStats(CityStats s)
        {
            int children = 0, adults = 0, seniors = 0, unLow = 0, unHigh = 0, students = 0;
            long happy = 0, edu = 0;
            int pop = 0;
            for (int i = 0; i < _cHigh; i++)
            {
                ref readonly Citizen c = ref _c[i];
                if (c.Household < 0 || (c.Flags & CitizenFlags.Zombie) != 0) continue;
                pop++;
                happy += c.Happiness;
                if (c.Age < 18) { children++; if (c.Workplace.IsValid) students++; }
                else if (c.Age >= 65) seniors++;
                else
                {
                    adults++;
                    edu += c.Education;
                    if (!c.Workplace.IsValid) { if (c.Education >= 2) unHigh++; else unLow++; }
                }
            }
            s.Population = pop;
            s.Children = children;
            s.Adults = adults;
            s.Seniors = seniors;
            s.Students = students;
            s.UnemployedLow = unLow;
            s.UnemployedHigh = unHigh;
            s.Households = _households;
            s.AverageHappiness = pop > 0 ? (float)happy / pop : 60f;
            s.AverageEducation = adults > 0 ? (float)edu / adults : 0f;
        }

        public ulong ComputeHash()
        {
            ulong h = HashUtil.Mix(0x504F5055UL, (ulong)_alive);
            h = HashUtil.Mix(h, _rng.State);
            h = HashUtil.Mix(h, _rng.Increment);
            h = HashUtil.MixF(h, _immigrationAcc);
            h = HashUtil.Mix(h, (ulong)(Born + Died * 31 + Immigrated * 977 + Emigrated * 7919));
            for (int i = 0; i < _cHigh; i++)
            {
                ref readonly Citizen c = ref _c[i];
                if (c.Household < 0) continue;
                h = HashUtil.Mix(h, (ulong)(uint)i | ((ulong)(uint)c.Household << 32));
                h = HashUtil.Mix(h, (ulong)(uint)c.Workplace.Index | ((ulong)c.Age << 32) | ((ulong)c.Education << 40) | ((ulong)c.Happiness << 48) | ((ulong)c.State << 56));
                h = HashUtil.Mix(h, (ulong)(uint)c.Location | ((ulong)(uint)c.LastTripDay << 32));
            }
            return h;
        }
    }
}
