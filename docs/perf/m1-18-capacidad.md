# Capacidad de red: medición antes y después (M1-18, 2026-11-05)

Método: `Sim.Runner --bench --quick` (semilla 1, release, 16 núcleos) con el commit anterior (36922d4) y con el cambio.

| Objetivo | Activos antes / después | Flujo % antes / después | ms/tick media antes / después | p95 antes / después |
|---|---|---|---|---|
| 1.000 | 990 / 991 | 52,9 / 55,1 | 0,566 / 0,580 | 0,691 / 0,716 |
| 3.000 | 2.972 / 2.974 | 51,6 / 54,1 | 0,752 / 0,703 | 1,033 / 0,927 |

Sin empeoramiento (±3 %, dentro del ruido); los bytes por tick no cambian. Capacidades aisladas y diagnóstico en ADR 004 ("Capacidad (M1-18)").
