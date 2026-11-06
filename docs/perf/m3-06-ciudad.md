# M3-06 · Bot de ciudad: 10.000 habitantes

Comando: `dotnet run --project sim/Sim.Runner -c Release -- --scenario city --seed 1 --max-days 500 --hold-ticks 3000 --csv docs/perf/m3-06-ciudad.csv --render docs/perf/m3-06-ciudad.png --verbose`

Máquina de desarrollo (PC, Release, un solo hilo). Datos reales de esa ejecución (semilla 1, hash final `2159DDCD3DC3005E`):

| Medida | Valor |
|---|---|
| Habitantes objetivo alcanzado | día 345 (1.247.201 ticks), 10.083 habitantes, hito Ciudad pequeña |
| Coste por tick hasta llegar | media 0,107 ms |
| Coste por tick con 10.083 habitantes (3.000 ticks) | media 0,198 ms, p95 0,289 ms, máx 0,683 ms |
| Vehículos físicos vivos a 10.000 hab. | 764 (tope 1.200); el resto de viajes es abstracto |
| Economía a ~9.000 hab. | ingresos 115.000 / semana, gastos 13.500 / semana, saldo 915.000 |
| Red | ~219 segmentos, 16,8 km de calles, 133 edificios de servicio |

Evolución (una fila por día en `m3-06-ciudad.csv`): 1.714 hab. en el día 41, 4.589 en el 161, 7.431 en el 241, 8.952 en el 321.

`m3-06-ciudad.png`: mapa final (verde vivienda, azul comercio, amarillo industria, cuadrados de colores servicios, negro centrales de carbón).

## Notas
- El ciclo completo se ve en el CSV: calles, zonas, agua y luz, inmigración, empleo, viajes, impuestos y mantenimiento.
- Equilibrio: el bot acaba con mucho dinero (1 M): la economía es holgada; ajustar `TaxPerOccupant` y los costes si se quiere más tensión. Con 1,2 M de ticks el bot nunca tuvo saldo negativo prolongado.
- Los viajes "perdidos" (5.753 en total) son viajes físicos que no consiguieron salir a tiempo; el ciudadano se teletransporta a su destino.
- Con el bot detenido en el día 120 aprox. de una versión anterior hubo estancamiento por falta de sitio para servicios (corregido con reintento junto a la última calle construida) y de distrito comercial (se reconvierten calles residenciales).
