# URBE — Game Design Document

> **Título provisional:** URBE (se puede cambiar en cualquier momento).
> **Versión:** 0.2 (decisiones del director aplicadas, ver [§20.2](#202-decisiones-tomadas-2026-10-28)) · **Fecha:** 2026-10-28
> **Director:** Álvaro · **Redacción técnica:** Claude
> **Documento hermano:** [PLAN_DE_TRABAJO.md](PLAN_DE_TRABAJO.md) (quién hace qué, en qué orden y con qué modelo de IA).

Este documento describe **qué** es el juego y **cómo** funciona por dentro. Es la fuente de verdad del diseño: si una tarea contradice este documento, se actualiza primero el documento.

---

## Índice

- [0. Resumen en un minuto](#0-resumen-en-un-minuto)
- [1. Visión y pilares](#1-visión-y-pilares)
- [2. Mercado y referencias](#2-mercado-y-referencias)
- [3. Modos de juego y progresión](#3-modos-de-juego-y-progresión)
- [4. Mecánicas principales](#4-mecánicas-principales)
- [5. Mecánica de construir](#5-mecánica-de-construir)
- [6. Ciudad viva: variedad de edificios](#6-ciudad-viva-variedad-de-edificios)
- [7. Animaciones baratas: VAT y shaders](#7-animaciones-baratas-vat-y-shaders)
- [8. Simulación: el núcleo](#8-simulación-el-núcleo)
- [9. Tráfico: herramientas y diagnóstico](#9-tráfico-herramientas-y-diagnóstico)
- [10. Mapas de calor y capas de información](#10-mapas-de-calor-y-capas-de-información)
- [11. Interfaz y experiencia de usuario](#11-interfaz-y-experiencia-de-usuario)
- [12. Menús completos y catálogo](#12-menús-completos-y-catálogo)
- [13. Arte y dirección visual](#13-arte-y-dirección-visual)
- [14. Sonido y música](#14-sonido-y-música)
- [15. Optimización y rendimiento](#15-optimización-y-rendimiento)
- [16. Tamaño y almacenamiento](#16-tamaño-y-almacenamiento)
- [17. Sin conexión y servicios opcionales](#17-sin-conexión-y-servicios-opcionales)
- [18. Monetización y distribución](#18-monetización-y-distribución)
- [19. Arquitectura técnica](#19-arquitectura-técnica)
- [20. Riesgos y decisiones](#20-riesgos-y-decisiones)
- [21. Glosario](#21-glosario)

---

## 0. Resumen en un minuto

**URBE es un city builder de simulación profunda, al estilo de Cities: Skylines, diseñado desde cero para el móvil.** Se juega con carreteras libres y curvas, ciudadanos con vida propia y un tráfico real cuyos atascos tienen consecuencias. Los edificios nunca son los mismos tres. No hay temporizadores, ni gemas, ni conexión obligatoria.

| Aspecto | Decisión |
|---|---|
| Plataformas | **Android** (principal) y **PC** (Windows; Linux/Steam Deck si sale barato). **Sin iOS** |
| Motor | **Unity 6 LTS** + URP, con la simulación en una librería C# independiente (`Sim.Core`) |
| Estilo visual | **Low-poly estilizado** con una textura de paleta compartida |
| Modelo de negocio | **Gratis con demo + desbloqueo único.** Sin anuncios, sin temporizadores, sin gemas |
| Conexión | **100 % offline.** Lo online (nube, mods) es siempre opcional |
| Público | Jugadores de Cities: Skylines, SimCity 4 o TheoTown que quieren profundidad en el móvil |

**Objetivos medibles** (se comprueban en cada hito):

| Métrica | Objetivo |
|---|---|
| Descarga base en Google Play | ≤ 150 MB |
| Rendimiento en móvil de gama media con 100.000 habitantes | 30 fps estables |
| Rendimiento en PC con 300.000 habitantes | 60 fps |
| Tamaño de partida guardada con 100.000 habitantes | ≤ 15 MB (objetivo interno: 2–5 MB) |
| Variantes visuales de edificios que crecen | ≥ 5.000 combinaciones distinguibles |
| Elementos colocables a mano | ≥ 180 |
| Carga de una partida de 100.000 habitantes en gama media | < 15 s |
| Funciones que necesitan internet | 0 |

---

## 1. Visión y pilares

> *"Que cada atasco tenga un porqué, que cada barrio tenga su cara y que todo quepa en el bolsillo."*

### Pilar 1 — Creatividad sin límites
- **En la práctica:**
  - Carreteras libres con curvas Bézier.
  - Plantillas, copiar y pegar, modo plano (borrador).
  - Sandbox con dinero infinito.
  - Modo diorama: simulación en pausa, todo desbloqueado.
  - Decoración libre: árboles, caminos, agua, terreno.
  - Estilos arquitectónicos por distrito.
- **Lo que NO haremos:** cuadrícula obligatoria, edificios fijos sin variación o capar la creatividad para vender atajos.

### Pilar 2 — Simulación con consecuencias
- **En la práctica:**
  - Cada ciudadano tiene casa, trabajo y horario.
  - Cada servicio (basura, bomberos, ambulancias, mercancías) es un vehículo real que circula por tus calles.
  - Si las calles se colapsan, los servicios no llegan, y eso tiene consecuencias.
- **Lo que NO haremos:** "coberturas mágicas" por radio que ignoran el tráfico.

### Pilar 3 — Ciudad viva
- **En la práctica:**
  - Edificios modulares procedurales con miles de variantes.
  - Peatones con actividades.
  - Luces nocturnas, terrazas, eventos y un feed de ciudadanos.
- **Lo que NO haremos:** filas de clones idénticos.

### Pilar 4 — Explicable
- **En la práctica:** cada problema muestra su **cadena de causas** y un toque te lleva al origen. Ejemplo:
  - **Problema:** "Edificio en llamas".
  - **Causa:** los bomberos llegaron 9 minutos tarde.
  - **Origen:** atasco en Av. del Puerto, que es el cruce problemático.
- **Lo que NO haremos:** iconos de queja sin contexto.

### Pilar 5 — Táctil de verdad
- **En la práctica:**
  - Interfaz pensada para el pulgar.
  - El cursor se desplaza por encima del dedo, con lupa de precisión.
  - Se confirma con ✓ y se puede construir en pausa.
  - Gestos coherentes.
- **Lo que NO haremos:** portar la interfaz de PC con botones diminutos.

### Pilar 6 — Ligero y offline
- **En la práctica:** descarga pequeña, partidas de pocos MB y sin conexión.
- **Lo que NO haremos:** descargas obligatorias de 2 GB, cuentas obligatorias o "siempre online".

### Pilar 7 — Respeto al jugador
- **En la práctica:** un único pago desbloquea todo, sin anuncios, sin esperas y sin dark patterns.
- **Lo que NO haremos:** temporizadores, energía, gemas o lootboxes.

---

## 2. Mercado y referencias

### 2.1 El hueco

En Android hay muchos juegos "de ciudades", pero casi todos son **tycoons de gestión con temporizadores**, como SimCity BuildIt o Megapolis, o **simuladores sencillos de cuadrícula**, como Pocket City. Los más profundos, como TheoTown, son 2D pixel art.

No hay un city builder **3D, con carreteras libres y simulación por agentes** al nivel de Cities: Skylines pensado para el móvil. Ese es nuestro hueco.

| Juego | Qué hace bien | Por qué decepciona | Qué tomamos |
|---|---|---|---|
| **Cities: Skylines** (PC/consola) | Simulación por agentes, carreteras libres, distritos y políticas, capas de información, mods | No existe en Android. Exige un PC potente. La IA de carriles es mala sin el mod TM:PE. Los problemas cuestan de entender | Casi todo el concepto, mejorando la IA de carriles, el diagnóstico y el rendimiento |
| **SimCity BuildIt** | Pulido, accesible | Temporizadores, producción de materiales, monetización agresiva | Nada del modelo; solo la claridad visual |
| **Pocket City / Pocket City 2** | Premium, sin trampas, buen táctil | Simulación superficial, casi en cuadrícula | Modelo premium y controles táctiles sencillos |
| **TheoTown** | Profundo, muchos edificios, mods | 2D pixel, sin carreteras curvas, interfaz densa | La variedad de edificios y el espíritu de los mods |
| **Designer City** | Libertad decorativa | Simulación casi decorativa, freemium | La libertad creativa |

### 2.2 Qué mejoramos respecto a Cities: Skylines

1. **Elección de carril inteligente de serie.** Los coches eligen carril con antelación según su ruta, para evitar el clásico "todos por el mismo carril".
2. **Aparcamiento abstracto.** Si no hay plazas, los coches buscan, lo que genera más tráfico, y se puede invertir en aparcamientos o en transporte público.
3. **Explicabilidad.** Cadena de causas, explorador de rutas integrado y un asesor de tráfico.
4. **Redes automáticas.** Las tuberías y el cableado van bajo las carreteras automáticamente, como en Cities: Skylines II. En el móvil, dibujar tuberías es tedioso.
5. **Rendimiento.** La población es virtual, los agentes tienen nivel de detalle y las animaciones van en la GPU (VAT).
6. **Tamaño.** Edificios procedurales a partir de semillas: partidas e instalación pequeñas.

### 2.3 Referencias abiertas para estudiar (ideas, no código)

| Proyecto | Licencia | Qué estudiar |
|---|---|---|
| Micropolis (SimCity original liberado) | GPLv3 | Modelo de demanda RCI y campos de rejilla |
| OpenTTD | GPLv2 | Pathfinding (YAPF), señales y redes de transporte |
| Simutrans | Artistic License | Transporte multimodal y pasajeros con destino |
| TM:PE (mod de C:S) | Código abierto | Qué herramientas de tráfico pedía la comunidad |
| Devlogs de Citybound | Públicos | Simulación por agentes y carreteras procedurales |

**Regla de oro:** no se copia código GPL en URBE, porque nos obligaría a liberar todo el juego, ni código o assets de juegos comerciales. Se aplica el **diseño en sala limpia**: observar el comportamiento, describirlo con palabras propias e implementarlo desde cero. **No se usan herramientas de ingeniería inversa** sobre juegos ni apps de terceros (ver [PLAN_DE_TRABAJO.md](PLAN_DE_TRABAJO.md)).

---

## 3. Modos de juego y progresión

### 3.1 Modos

| Modo | Descripción |
|---|---|
| **Carrera** | Empiezas con dinero limitado en una casilla de 2×2 km. Los hitos de población desbloquean servicios, densidades y más casillas de mapa. Es el modo principal |
| **Sandbox** | Dinero infinito, todo desbloqueado y simulación completa. Para construir la ciudad de tus sueños |
| **Diorama** | Simulación en pausa permanente, todo desbloqueado. Construcción y decoración pura, con modo foto. Ideal para crear sin presión |
| **Escenarios** | Ciudades ya hechas con un problema que resolver. Ejemplos: "El atasco eterno: lleva el flujo de tráfico al 75 % en 2 años", "Isla sin coches", "100 % renovable", "Ciudad en bancarrota" |
| **Editor de mapas** (después del lanzamiento) | Terreno, agua, recursos naturales y conexiones exteriores |

### 3.2 Opciones de partida (dificultad a medida)

- **Economía:** fácil, normal o difícil (multiplica costes e ingresos).
- **Vehículos atascados:**
  - *Realista:* nunca desaparecen.
  - *Normal:* desaparecen tras 10 minutos de juego atascados.
  - *Relajado:* desaparecen tras 3 minutos.
- **Desastres:** apagados por defecto. Se pueden activar uno a uno.
- **Construcción:** instantánea o con obras visibles (por defecto, obras visibles solo en los edificios que crecen).
- **Desbloqueos:** por hitos o todo desbloqueado.
- **Estilo arquitectónico inicial:** ver [6.4](#64-estilos-arquitectónicos).
- **Semilla de la partida:** compartible, para jugar el mismo mapa y la misma variación.

### 3.3 Hitos de población

| # | Nombre | Población | Desbloqueos principales |
|---|---|---|---|
| 0 | Asentamiento | 0 | Calles básicas, zonas de baja densidad (R/C/I), agua de río, eólica pequeña, carbón, desagüe |
| 1 | Aldea | 400 | Vertedero, centro de salud, parque de barrio, políticas básicas |
| 2 | Pueblo | 1.000 | Bomberos, policía de barrio, colegio, distritos, autobús |
| 3 | Villa | 2.500 | Cementerio, clínica, comisaría, parques medianos, 2.ª casilla de mapa |
| 4 | Ciudad pequeña | 5.000 | Densidad media, instituto, oficinas, avenidas de 4 carriles, solar |
| 5 | Ciudad | 10.000 | Alta densidad, hospital, tranvía, industria especializada, 3.ª casilla |
| 6 | Ciudad grande | 20.000 | Metro, universidad, incineradora, gas, centro de control de tráfico |
| 7 | Gran ciudad | 40.000 | Tren de cercanías y mercancías, puerto, aeródromo, 4.ª–5.ª casillas |
| 8 | Capital | 70.000 | Monumentos únicos (1.ª tanda), nuclear, desaladora |
| 9 | Metrópolis | 100.000 | Aeropuerto internacional, monumentos (2.ª tanda), casillas 6–9 |
| 10 | Megalópolis | 200.000 | Monumentos finales y planta de fusión (en móvil, recomendado solo en gama alta) |

**Mapa:** casillas de 2×2 km. En el móvil, hasta **9 casillas** (6×6 km); en PC, hasta **25 casillas** (10×10 km). El límite del móvil se puede subir en gama alta.

### 3.4 Logros locales (offline)

Ejemplos:
- "Hora punta sin drama": flujo de tráfico ≥ 80 % con 50.000 habitantes.
- "Ciudad de 15 minutos": el 70 % de los ciudadanos tiene trabajo, colegio y tienda a menos de 15 minutos andando o en transporte público.
- "Cielo limpio": cero contaminación del aire con 20.000 habitantes.
- "Arquitecto ecléctico": 5 estilos en una misma ciudad.
- "Sin coches": un distrito de 5.000 habitantes solo peatonal.
- "De la nada": llegar a 10.000 habitantes sin préstamos.

Se guardan en local. Si hay Google Play Games o Steam conectado, se sincronizan de forma opcional.

---

## 4. Mecánicas principales

### 4.1 Terreno y agua

- **Terreno:** mapa de alturas con resolución de 4 m por celda en el mapa jugable y menor en el horizonte. Cada casilla de 2 km tiene 512×512 muestras.
- **Agua:**
  - Ríos, lagos y mar con nivel estático y corriente simplificada para la contaminación aguas abajo.
  - No hay simulación de fluidos en el móvil.
  - Las inundaciones solo existen como desastre opcional, con un modelo simplificado por celdas.
- **Recursos naturales** (mapas en el terreno): tierra fértil, bosque, petróleo, mineral y agua subterránea. Condicionan dónde funciona cada industria especializada.
- **Viento:** dirección dominante por mapa con variaciones lentas. Afecta a la contaminación del aire y a la eficiencia de los aerogeneradores.

### 4.2 Carreteras

Las carreteras son **segmentos Bézier cúbicos** entre nodos, con carriles definidos en datos (JSON). Tipos al lanzamiento:

| Tipo | Carriles | Velocidad | Capacidad relativa | Ruido | Zonificable | Notas |
|---|---|---|---|---|---|---|
| Calle de 2 carriles | 1+1 | 40 | 1,0 | Bajo | Sí | La básica |
| Calle con árboles | 1+1 | 40 | 1,0 | Muy bajo | Sí | Más valor del suelo |
| Calle de sentido único | 2 | 40 | 1,3 | Bajo | Sí | |
| Calle con carril bici | 1+1 (+bici) | 40 | 1,0 | Bajo | Sí | |
| Avenida de 4 carriles | 2+2 | 50 | 2,2 | Medio | Sí | |
| Avenida con mediana arbolada | 2+2 | 50 | 2,2 | Medio | Sí | Impide girar a la izquierda a mitad de tramo |
| Avenida con carril bus | 2+2 (1 bus por sentido) | 50 | 2,0 | Medio | Sí | |
| Avenida con tranvía | 2+2 + tranvía | 50 | 2,0 | Medio | Sí | |
| Gran avenida de 6 carriles | 3+3 | 60 | 3,4 | Alto | Sí | |
| Sentido único ancho | 3 | 60 | 2,6 | Medio | Sí | |
| Carretera rural | 1+1 | 80 | 1,1 | Medio | No | Arcén, sin aceras |
| Autopista | 2+2 | 100 | 4,0 | Muy alto | No | Solo enlaces |
| Autopista ancha | 3+3 | 120 | 6,0 | Muy alto | No | |
| Rampa de autopista | 1 o 2 | 60 | 1,5 | Alto | No | |
| Calle peatonal | — (servicios y emergencias permitidos) | 20 | — | Ninguno | Sí | Clave para los barrios sin coches |
| Camino peatonal | — | — | — | Ninguno | No | También parques y escaleras |
| Carril bici independiente | — | — | — | Ninguno | No | |

- **Elevación:** cada tipo puede ir a nivel, **elevado** (puente o viaducto) o **soterrado** (túnel). Se generan solos al cruzar agua o al pulsar los botones de altura (ver [5.3](#53-carreteras)).
- **Actualizar:** se cambia el tipo sin demoler, conservando los edificios si el ancho lo permite.
- **Nombres:** se generan al azar desde listas temáticas por estilo y se pueden editar.

### 4.3 Intersecciones y control de tráfico

- Las intersecciones se generan **automáticamente** al cruzar o unir vías: geometría de la acera, pasos de peatones, flechas por defecto.
- **Control por intersección:**
  - **Prioridad:** automática según la jerarquía de las vías; se puede editar a ceda o stop.
  - **Semáforos:** automático, tiempos fijos editables o adaptativo. El adaptativo se desbloquea con el Centro de control de tráfico.
  - **Flechas de carril:** qué carril puede girar adónde.
  - **Conexiones de carril:** editor avanzado carril a carril.
  - **Prohibiciones:** giro a la izquierda, cambio de sentido, paso de camiones.
  - **Pasos de peatones:** activar o desactivar cada uno.
- **Rotondas:** herramienta dedicada con 4 tamaños y ceda al entrar.
- **Enlaces prefabricados de autopista:** diamante, trompeta, trébol, glorieta partida y turbina. Ver [5.7](#57-plantillas-copiar-pegar-y-simetría).

### 4.4 Zonificación

Las zonas se pintan sobre **celdas de 8×8 m** que se generan a los lados de las carreteras zonificables, con hasta 4 celdas de fondo.

| Zona | Densidades | Subtipos | Notas |
|---|---|---|---|
| **Residencial (R)** | Baja, media, alta | Normal, ecológica (más tarde) | Hogares |
| **Comercial (C)** | Baja, alta | Barrio, ocio (por la noche), turismo | Tiendas; necesitan mercancías y clientes |
| **Industrial (I)** | Única | Genérica + especializadas (agrícola, forestal, petróleo, minera) | Empleo poco cualificado; produce mercancías y contaminación |
| **Oficinas (O)** | Media, alta | Normal, tecnológica | Empleo cualificado; sin mercancías |
| **Mixta (R+C)** | Media, alta | Bajos comerciales y viviendas encima | **Novedad creativa:** calles de ciudad europea de verdad |

- **Tamaño de parcela:** de 1×1 a 4×4 celdas. Los edificios eligen parcela según la demanda y el nivel.
- La zona se puede **repintar**. Si cambias la zona de un edificio existente, este acaba abandonando el sitio y se reemplaza.

### 4.5 Edificios que crecen y niveles

Los edificios de las zonas aparecen solos si hay **demanda**, **acceso por carretera**, **electricidad** y **agua**. Suben de **nivel (1–5)** cuando se cumplen estos requisitos durante un tiempo sostenido:

| Nivel | Valor del suelo | Servicios | Educación media (residencial y oficinas) |
|---|---|---|---|
| 1 | Cualquiera | Agua y electricidad | — |
| 2 | ≥ 20 | + basura y salud cubiertas | ≥ 0,5 |
| 3 | ≥ 40 | + policía, bomberos y un parque cerca | ≥ 1,0 |
| 4 | ≥ 60 | + transporte público a ≤ 5 min | ≥ 1,8 |
| 5 | ≥ 80 | + ocio y cultura cerca, ruido bajo | ≥ 2,4 |

Al subir de nivel cambia el modelo procedural (más alto, mejores acabados), aumentan los habitantes, la recaudación y la demanda de servicios.

### 4.6 Distritos y políticas

Se pintan con un pincel. Cada distrito tiene nombre, estadísticas propias y **políticas**:

| Política | Efecto |
|---|---|
| Prohibir camiones pesados | Los camiones rodean el distrito; menos ruido |
| Centro peatonal | Calles peatonales con acceso solo para servicios y entregas en horario |
| Entregas nocturnas | Los camiones de mercancías solo circulan de noche (hay que saber dónde aparcarlos) |
| Zona de bajas emisiones | Menos contaminación y coches; más demanda de transporte público |
| Tasa de congestión | Peaje por entrar en coche: ingresos y menos tráfico |
| Fomento de la bici | Más viajes en bici si hay carriles bici |
| Transporte público gratuito | Más usuarios, menos ingresos |
| Límite de altura | Los edificios no pasan de N plantas (estética) |
| Estilo arquitectónico | Fuerza el estilo de los edificios nuevos del distrito |
| Eficiencia energética | −15 % de consumo, +coste de construcción |
| Reciclaje obligatorio | −30 % de basura al vertedero, +coste |
| Alarmas de humo | −50 % de riesgo de incendio, +coste |
| Educación gratuita / becas | +educación, −ingresos |
| Ruido nocturno restringido | Menos ocio nocturno; más felicidad residencial |
| Turismo | +turistas, +tráfico, +ingresos comerciales |
| Huertos urbanos | +felicidad, algo de comida local |

### 4.7 Servicios urbanos

Todos los servicios funcionan con **vehículos o personas reales** que se desplazan por la red (ver [8.9](#89-servicios-con-despacho)). La cobertura que ves no es un radio: es el **tiempo de llegada real** desde la base (isócrona).

- **Agua:**
  - Bombeo de río o de pozo, potabilizadora y depósitos.
  - Si se bombea aguas abajo de un desagüe, el agua sale contaminada y los ciudadanos enferman.
- **Alcantarillado:** el desagüe contamina el río; la depuradora reduce esa contaminación.
- **Electricidad:**
  - La demanda varía de día y de noche, y la solar no produce de noche.
  - Las baterías de red almacenan energía.
  - Un déficit provoca apagones por zonas.
- **Basura:**
  - Los camiones recogen por rutas.
  - Vertedero (se llena), incineradora (genera energía y contamina) y reciclaje.
- **Salud:**
  - Las ambulancias recogen enfermos; los centros tienen camas.
  - Las residencias de mayores mejoran la esperanza de vida.
- **Muerte:**
  - Los coches fúnebres recogen a los fallecidos.
  - Cementerio (se llena) o crematorio.
  - Un cadáver sin recoger provoca problemas sanitarios en el edificio.
- **Bomberos:**
  - Los camiones apagan incendios.
  - El fuego se propaga a los edificios vecinos si tardan.
  - Los incendios forestales son opcionales.
- **Policía:** patrullas reducen el crimen; las detenciones van a la comisaría y luego a la prisión.
- **Educación:**
  - Plazas por centro; los alumnos viajan cada día.
  - Niveles: 0 sin estudios, 1 primaria, 2 secundaria y FP, 3 universidad.
- **Parques y ocio:** atraen visitantes (viajes de ocio), suben el valor del suelo y la felicidad.
- **Administración:**
  - El ayuntamiento desbloquea las políticas.
  - El centro de control de tráfico activa los semáforos adaptativos.
  - La estación meteorológica da previsiones.

### 4.8 Transporte público

| Modo | Infraestructura | Capacidad por vehículo | Notas |
|---|---|---|---|
| Autobús | Paradas en la acera; cochera | 40 | Se ve afectado por el tráfico salvo que tenga carril bus |
| Trolebús (después del lanzamiento) | Catenaria en la calle | 45 | Sin emisiones |
| Tranvía | Vías en la calle o propias; cochera | 120 | Prioridad semafórica opcional |
| Metro | Túnel o elevado; estaciones | 300 | No sufre el tráfico |
| Tren de cercanías | Vías; estaciones | 400 | También conecta con el exterior |
| Ferry | Muelles | 150 | Para mapas con agua |
| Taxi | Cochera y paradas | 3 | Útil en zonas sin transporte |
| Bici compartida | Estaciones | 1 | Viajes cortos |
| Teleférico | Estaciones en altura | 30 | Para mapas con montaña |

- **Líneas:** se tocan las paradas en orden y la línea se cierra sola.
- Frecuencia (número de vehículos), presupuesto, tarifa, color y nombre.
- **Viajes multimodales:** andar → bus → metro → andar.

### 4.9 Industria y cadena de mercancías

```
Materias primas (granja, bosque, petróleo, mina)
        │ camiones o tren
        ▼
Procesado (silo, aserradero, refinería, procesadora de minerales)
        │
        ▼
Fábricas (alimentos, muebles, plásticos, tecnología) ──► Exportación (autopista, tren, puerto)
        │ camiones de reparto
        ▼
Comercio (tiendas, centros comerciales) ◄── Importación si falta producción local
```

- Un comercio sin mercancías pierde clientes y acaba cerrando.
- Una industria sin trabajadores o sin salida para sus productos también.
- Los camiones son vehículos reales, así que una mala logística genera atascos.

### 4.10 Economía

- **Ingresos:**
  - Impuestos por zona y densidad (1–29 %, por defecto 9 %).
  - Billetes de transporte, tasas de congestión, venta de electricidad o agua sobrante a la región, turismo y exportaciones.
- **Gastos:**
  - Mantenimiento por edificio de servicio.
  - Presupuesto por servicio con un deslizador del 50 al 150 %: afecta a la eficiencia y al número de vehículos.
  - Intereses de los préstamos.
- **Préstamos:** 3 bancos con distinto plazo e interés.
- **Informes:** semanal (gráfico) y trimestral (desglose).
- **Bancarrota:** tras 2 meses en negativo, intervención con escenario de rescate. Opcional: desactivable.
- **Sandbox:** dinero infinito; la economía se muestra solo como información.

### 4.11 Ciudadanos

- **Ciclo vital:** niño → adolescente → adulto → mayor → fallecimiento. Nacen niños en los hogares.
- **Atributos:** edad, educación (0–3), salud, felicidad, riqueza del hogar, trabajo o colegio, vehículo propio (sí o no) y preferencia de transporte.
- **Rutina:** casa → trabajo o colegio → compras u ocio → casa, con variación aleatoria y fines de semana.
- **Migración:**
  - Llegan ciudadanos si hay demanda residencial y atractivo.
  - Se van si la felicidad es baja de forma sostenida.
- **Turistas:** llegan por autopista, tren, puerto o aeropuerto. Visitan monumentos y ocio, y duermen en hoteles.

### 4.12 Problemas de los edificios y sus causas

| Problema | Causas posibles que se muestran en la cadena |
|---|---|
| Sin electricidad | Déficit de producción · edificio desconectado de la red · apagón por avería |
| Sin agua | Déficit de bombeo · desconectado · agua contaminada |
| Basura acumulada | Camiones insuficientes · camión atascado (ruta X) · vertedero lleno |
| Fallecido sin recoger | Coches fúnebres insuficientes · cementerio lleno · atasco |
| Enfermos | Contaminación del agua o del aire · ruido · ambulancia tardía |
| Crimen alto | Poca policía · paro alto · baja educación |
| Incendio | Riesgo alto (sin alarmas, edificio viejo) · bomberos tardíos |
| Faltan trabajadores | Población sin la educación adecuada · los trabajadores no llegan (atasco o sin transporte) |
| Faltan clientes | Comercio aislado · competencia · poca población cercana |
| Faltan mercancías | Industria insuficiente · camiones atascados · importación bloqueada |
| Ruido | Vía muy transitada · industria o ocio cerca |
| Abandonado | Cualquier problema mantenido durante N días |

### 4.13 Eventos y vida

- **Eventos programables:** partido en el estadio, concierto, festival, mercadillo, desfile (cortes de calle), maratón (cortes de ruta). Generan picos de tráfico que el jugador debe prever.
- **Eventos espontáneos:** atracciones turísticas virales, huelga del transporte si su presupuesto es bajo, ola de calor (más consumo eléctrico) y fiestas de barrio.
- Ningún evento obliga a pagar ni a esperar.

### 4.14 Tiempo, día y noche, clima

- **Calendario:** días, semanas, meses y años. **1 día de juego ≈ 6 minutos reales a ×1** (configurable: 3, 6 o 12 minutos).
- **Ciclo de día y noche:** iluminación, ventanas encendidas, tráfico nocturno distinto y ocio nocturno.
- **Clima:** despejado, nublado, lluvia (menos velocidad y más accidentes leves), niebla y nieve (opcional y estética, con quitanieves en una expansión).
- **Estaciones:** estéticas y opcionales (colores de los árboles, nieve en los tejados mediante shader).

### 4.15 Desastres (opcionales, apagados por defecto)

Incendio forestal, inundación, terremoto, tormenta o tornado y apagón general. Cada uno se puede activar o desactivar, con frecuencia baja, media o alta. Hay sistemas de alerta temprana desbloqueables.

---

## 5. Mecánica de construir

### 5.1 Principios

1. **Ver antes de pagar.** Todo se previsualiza como "fantasma": coste, cobertura, impacto en los mapas de calor y motivo si no se puede colocar.
2. **Precisión con el dedo.** El dedo nunca tapa lo que construyes; siempre hay forma de afinar.
3. **Sin miedo a equivocarse.** Deshacer, devolución completa durante una ventana corta y modo plano.
4. **Pocos toques.** Las cosas habituales (cuadrícula de calles, rotonda, enlace de autopista) en 2–3 toques.
5. **Igual en PC.** Las mismas herramientas con ratón, teclado y atajos.

### 5.2 Flujo común de todas las herramientas

```
[Elegir herramienta] → [Fantasma sigue al cursor] → [Ajustar: rotar, curvar, elevar]
        → [Validación en tiempo real: verde / rojo + motivo] → [Confirmar ✓ (móvil) o clic (PC)]
        → [Se crea un Comando] → [Sim.Core lo valida y aplica] → [Se apila en Deshacer]
```

- **Motivos de rojo** (siempre en texto): "Pendiente demasiado alta (14 % > 10 %)", "Ángulo demasiado cerrado con la calle existente", "Choca con: Colegio Las Acacias", "Fuera de tus casillas de mapa", "Sin dinero suficiente (faltan 12.400 €)".
- **Coste en vivo:** construcción + mantenimiento semanal, junto al cursor.

### 5.3 Carreteras

**Modos de trazado:**

| Modo | Uso |
|---|---|
| Recta | Arrastrar del punto A al B |
| Curva | A → punto de control → B (Bézier cuadrática convertida a cúbica) |
| Libre | Arrastre continuo; se simplifica a segmentos Bézier suaves al soltar |
| Arco de radio constante | Para curvas de autopista perfectas |
| **Cuadrícula** | Se dibuja un rectángulo y se generan las calles en cuadrícula (tamaño de manzana configurable: 4×4, 6×6 u 8×8 celdas) |
| Paralela | Duplica una vía a una distancia fija (útil para autopistas y avenidas) |
| Actualizar | Pasas el dedo por encima y cambia el tipo, el sentido o los carriles |

**Ayudas:**
- Ajuste a ángulos de 15° (90° y 45° destacados) y a la prolongación de otras vías (líneas guía).
- Longitud y ángulo visibles mientras se dibuja.
- Intersecciones automáticas al cruzar.
- Ángulo mínimo de 25° en cruces, para evitar intersecciones imposibles.
- **Pendiente máxima** por tipo: 10 % calles, 6 % autopista. Al superarla, el fantasma se pone rojo y sugiere elevar.
- **Elevación:** botones ▲▼ para pasar a puente o túnel (pasos de 4 m). Al cruzar agua se eleva automáticamente.
- **Sentido:** botón ⇄ para invertir las vías de sentido único.

### 5.4 Zonas

- **Generación de celdas:** cada carretera zonificable crea celdas de 8×8 m a ambos lados, hasta 4 de fondo. Las celdas en conflicto (curvas cerradas, solapes) se descartan.
- **Herramientas:**

  | Herramienta | Comportamiento |
  |---|---|
  | Relleno | Toca una manzana y se rellena toda |
  | Pincel | Tres tamaños |
  | Lazo | Dibujas un contorno y se zonifica el interior |
  | Borrar | Quita la zona |

- **Vista previa:** al elegir una zona se muestra la barra de demanda RCI y un mapa de "buen sitio para esta zona" (valor del suelo, ruido, contaminación).

### 5.5 Colocar edificios de servicio y únicos

- **Ajuste a la carretera:** el edificio gira solo para mirar a la vía más cercana; se puede girar a mano con dos dedos o con la rueda en PC.
- **Aplanado automático:** el terreno bajo la parcela se nivela y el coste se incluye en la vista previa.
- **Vista previa de impacto, la estrella de la casa:**
  - Una comisaría muestra su **isócrona real** de 3, 6 y 9 minutos según el tráfico actual.
  - Una incineradora muestra la **pluma de contaminación** prevista según el viento y el **ruido**.
  - Un parque muestra el aumento de valor del suelo previsto.
  - Una parada de bus muestra la población a menos de 5 minutos andando.
- **Duplicar:** colocar otro igual sin volver al menú (modo "pincel de edificios").

### 5.6 Modo plano (borrador)

- Un interruptor convierte cualquier herramienta en **planificación**: lo que construyes queda como **fantasma azul** que no cuesta dinero ni afecta a la simulación.
- Panel del plano: coste total, mantenimiento previsto, lista de elementos y orden de construcción.
- Opciones:
  - **Construir todo:** si hay dinero.
  - **Construir por fases:** se construye a medida que entra dinero, en el orden indicado.
  - **Descartar.**
- Los planos se guardan con la partida y se pueden convertir en plantilla.

### 5.7 Plantillas, copiar, pegar y simetría

- **Plantillas de fábrica:**
  - Rotondas (4 tamaños), turbo-rotonda.
  - Enlaces de autopista: diamante, trompeta, trébol, glorieta partida, turbina.
  - Manzanas tipo ensanche con chaflán.
  - Plazas.
- **Plantillas propias:**
  - Se selecciona un área, se guarda con nombre y miniatura, y se coloca rotada o reflejada.
  - Se guardan en el dispositivo, son ligeras (solo comandos y referencias) y se pueden exportar e importar como archivo `.urbeplan`.
- **Copiar y pegar:** selección rectangular o de lazo; se pega con previsualización y coste.
- **Simetría:** espejo respecto a un eje o radial (3–8 brazos) para diseñar plazas y glorietas espectaculares.

### 5.8 Mover, demoler y deshacer

| Acción | Regla |
|---|---|
| **Mover** | Edificios colocados a mano y carreteras sin edificios encima. Cuesta el 20 % del precio |
| **Demoler** | Toque largo o herramienta de demolición. Devolución del 100 % si se demuele en los primeros 5 minutos reales tras construir, o antes de que acaben las obras; después, el 0 % |
| **Demoler por área** | Lazo, con confirmación y lista de lo que se va a perder |
| **Deshacer y rehacer** | Sandbox y diorama: ilimitado. Carrera: hasta 20 acciones o 5 minutos reales, lo que llegue antes. Se deshace también el dinero |

### 5.9 Terreno, agua, árboles y decoración

- **Terreno:** subir, bajar, aplanar a una altura, suavizar y rampa entre dos puntos. Pinceles de 3 tamaños y 3 fuerzas.
- **Agua (sandbox y diorama):** pintar fuentes de agua y lagos. Carrera: limitado a estanques decorativos.
- **Árboles:** pincel con mezcla de especies, densidad y aleatorización de tamaño.
- **Decoración:** farolas, bancos, fuentes, estatuas, vallas, caminos y pérgolas, con ajuste a la cuadrícula opcional.

### 5.10 Distritos y líneas de transporte

- **Distritos:** pincel que pinta áreas de forma libre; el nombre se genera solo y es editable.
- **Líneas:** se tocan las paradas existentes o se crean nuevas, en orden. Botón "Cerrar línea". Se elige el color y el número de vehículos.

### 5.11 Construcción en el tiempo

- **Edificios que crecen:** solar → cimientos → estructura con andamios → acabado. Las fases son visibles mediante un parámetro del shader más una malla de andamios instanciada.
- **Edificios de servicio:** instantáneos por defecto. Si la opción de partida está activa, tienen una obra corta (1–3 días de juego) con grúa animada por shader.
- Las obras generan algo de ruido y camiones de obra (opcional, gama media o superior).

### 5.12 Controles táctiles específicos

| Gesto o ayuda | Comportamiento |
|---|---|
| Cursor desplazado | El punto de construcción queda unos 60 px por encima del dedo (configurable; se invierte para zurdos) |
| Lupa de precisión | Al mantener el dedo quieto 0,4 s aparece una lupa ×2 |
| Confirmación ✓ | Los toques sueltos no construyen: se coloca el fantasma y se confirma con ✓ (desactivable para expertos) |
| Un dedo y dos dedos | Con una herramienta activa, un dedo usa la herramienta y dos dedos mueven la cámara. Así nunca hay conflicto |
| Desplazamiento en los bordes | Al arrastrar cerca del borde, la cámara se desplaza |
| Vibración | Pulso suave al ajustarse a un ángulo, guía o carretera |
| Construir en pausa | Un botón pausa la simulación mientras la herramienta está activa (opción por defecto en móvil) |
| Modo una mano | Barra y paneles en el lado del pulgar; botón flotante de herramienta |

### 5.13 Controles en PC

- **Cámara:** WASD o flechas para mover; Q/E para rotar; R/F o la rueda para el zoom; botón central para inclinar.
- **Herramientas:**
  - Clic izquierdo usa, clic derecho cancela.
  - Shift desactiva el ajuste automático; Ctrl alterna el ajuste a la cuadrícula.
  - Page Up y Page Down cambian la elevación.
  - Ctrl+Z y Ctrl+Y deshacen y rehacen.
  - Ctrl+C y Ctrl+V copian y pegan.
  - B activa el modo plano.
- Los atajos completos se pueden reasignar en Ajustes.

### 5.14 Implementación técnica

- **Patrón Command.** Cada acción del jugador es un comando serializable:
  - `BuildRoad`, `UpgradeRoad`, `PlaceBuilding`, `MoveBuilding`, `Demolish`, `PaintZone`, `PaintDistrict`, `SetPolicy`, `EditJunction`, `CreateTransitLine`, `TerraformBrush`, `PlaceProps`, `PlaceTemplate`.
  - Cada comando tiene `Validate(SimState) → Result` (motivo legible) y `Apply(SimState) → InverseCommand` (para deshacer).
- **Validación en `Sim.Core`,** no en Unity: así se puede testear en la nube y nunca hay divergencia entre lo que la interfaz cree y lo que la simulación acepta.
- **Rejilla espacial** (spatial hash) de 32 m para colisiones de huellas, segmentos y celdas de zona. Consultas O(1) amortizado.
- **Geometría de carreteras:**
  - Las mallas se generan por segmento (extrusión de un perfil por la curva Bézier) y por intersección (polígono de unión).
  - Se cachean y se regeneran solo los segmentos afectados.
- **Planos y plantillas:** listas de comandos con coordenadas relativas; no se ejecutan hasta confirmar.
- **Registro de comandos:** se guarda un pequeño historial reciente para depurar y para hacer pruebas de repetición (ver [19.6](#196-estrategia-de-pruebas)).

---

## 6. Ciudad viva: variedad de edificios

### 6.1 El problema

En muchos city builders, una ciudad grande se ve como **las mismas 3–10 casas copiadas** cientos de veces. Hacer cientos de edificios a mano es caro y aumenta el tamaño del juego. La solución: **edificios modulares procedurales**, que son baratos en espacio y casi infinitos en variedad.

### 6.2 Edificios modulares procedurales

Cada edificio que crece se **ensambla** a partir de un kit de piezas siguiendo una **gramática de forma** determinista:

```
Parcela (ancho × fondo, pendiente, esquina sí/no)
  └─ Volumetría (según zona, densidad, nivel): podio + cuerpo + retranqueos + torre
       └─ Plantas (planta baja especial: portal, local comercial, garaje)
            └─ Fachada por módulos de 3 m (tipo de ventana, balcón, cierre, mirador)
                 └─ Cubierta (plana, a 2 o 4 aguas, mansarda, ajardinada)
                      └─ Accesorios (antenas, aire acondicionado, depósitos, placas solares, toldos, rótulos, macetas)
```

- **Semilla por edificio:** `semilla = hash(semillaPartida, idParcela, nivel)`. La misma semilla siempre produce el mismo edificio. Por eso **no hace falta guardar la geometría** (ver [16](#16-tamaño-y-almacenamiento)).
- **Ensamblado:**
  - Se hace en un hilo de fondo (Jobs).
  - El resultado es una **lista de instancias de piezas** (pieza, transformación, tinte), no una malla nueva.
  - Las piezas se dibujan con instancing: 10.000 edificios pueden compartir 400 mallas.
- **Edificios de esquina:** variantes de fachada en chaflán o en L cuando la parcela toca dos calles.
- **Reglas de coherencia:**
  - Un mismo estilo comparte paleta y tipo de cubierta.
  - Los edificios vecinos evitan repetir la misma combinación (la semilla se recalcula si coincide con la de un vecino).

### 6.3 Ejes de variación

| Eje | Valores | Afecta a |
|---|---|---|
| Zona | R, C (barrio, ocio, turismo), I (genérica y 4 especializadas), O (normal, tecnológica), Mixta | Tipología (vivienda, tienda, nave…) |
| Densidad | Baja, media, alta | Número de plantas y volumetría |
| Nivel | 1–5 | Acabados, altura, accesorios, calidad |
| Parcela | 16 tamaños (1×1 a 4×4) | Huella y número de módulos de fachada |
| Estilo | 5 en la 1.0 (8 previstos) | Piezas, paleta, cubiertas |
| Riqueza del barrio | Baja, media, alta | Accesorios, jardines, coches aparcados |
| Semilla | Infinita | Combinación concreta de piezas, colores y accesorios |

Con unas **300 piezas en la 1.0** (unas 55 por estilo × 5 estilos, más las comunes), el número de combinaciones distinguibles supera con creces las **5.000 por estilo**. El objetivo de calidad es que en una manzana de 20 edificios **no haya dos iguales** a simple vista.

### 6.4 Estilos arquitectónicos

| Estilo | Rasgos | Cubiertas | Paleta | Versión |
|---|---|---|---|---|
| **Mediterráneo** | Encalado, persianas, balcones de forja, patios | Teja árabe a 2 o 4 aguas, azoteas | Blancos, ocres, terracota, azul añil | **1.0** |
| **Ensanche europeo** | Manzana cerrada, chaflán, miradores, bajos comerciales | Mansarda, cubierta plana | Piedra, crema, verde botella | **1.0** |
| **Nórdico** | Madera y ladrillo, ventanas grandes, colores vivos | 2 aguas muy inclinadas | Rojo Falun, amarillo, gris | **1.0** |
| **Suburbio norteamericano** | Casas con jardín y garaje, porches | 2 y 4 aguas de tejas planas | Pastel, blanco, gris | **1.0** |
| **Asia densa** | Torres estrechas, rótulos, terrazas, aire acondicionado por todas partes | Plana con depósitos | Gris hormigón, neones | Después |
| **Latinoamericano** | Colores intensos, rejas, patios, azoteas habitadas | Plana y teja | Turquesa, rosa, amarillo | **1.0** |
| **Modernista y brutalista** | Hormigón, volúmenes rotundos, pilotis | Plana | Grises cálidos | Después |
| **Eco-futurista** (desbloqueable) | Fachadas verdes, madera laminada, placas solares | Ajardinada | Verdes, madera, blanco | Después |

- **Estilo por distrito:** se elige con la política "Estilo arquitectónico". Sin política, se mezclan según el estilo inicial de la partida.
- **Mezcla controlada:** con la opción "ciudad ecléctica", cada barrio puede tener un estilo dominante (70 %) y uno secundario (30 %).
- **Después del lanzamiento:** Asia densa, Modernista y brutalista y Eco-futurista, como actualizaciones o DLC estético (ver [18](#18-monetización-y-distribución)). La comunidad podrá añadir más como mods de datos (en móvil y PC).

### 6.5 Edificios únicos hechos a mano

Los servicios, los monumentos y los edificios emblemáticos (más de 80 al lanzamiento, ver [12.8](#128-catálogo-de-elementos-colocables)) se modelan a mano o se generan con IA y se pulen. Siempre usan la misma paleta y las mismas reglas de polígonos. Muchos servicios tienen **variantes por estilo**: un colegio mediterráneo no es igual que uno nórdico.

### 6.6 Variación gratis en el shader

Sin coste de memoria ni de CPU apreciable:

| Efecto | Cómo se hace |
|---|---|
| Tinte por instancia | 3 canales de tinte (fachada, carpintería, toldos o cubierta) en los datos por instancia, aplicados sobre zonas marcadas en la textura de paleta |
| Envejecimiento y suciedad | Parámetro 0–1 por instancia que oscurece las bases y añade manchas mediante ruido |
| Ventanas iluminadas de noche | Hash por ventana (posición de mundo + UV) que decide si está encendida; parpadeo lento al anochecer |
| Nieve en los tejados | Las normales que miran hacia arriba se blanquean según el parámetro global de estación |
| Abandono | Desaturación, cristales rotos (máscara) y vegetación (decal) |
| Obras | Recorte vertical por altura de obra + malla de andamios instanciada |

### 6.7 Detalles de vida

- **Mobiliario urbano** por zona y estilo: farolas, bancos, papeleras, marquesinas, kioscos, buzones, fuentes y bolardos. Se colocan de forma procedural al construir la calle.
- **Coches aparcados:** en la calle y en los aparcamientos, según la ocupación real (simulación) e instanciados.
- **Terrazas:** mesas con gente de día en las zonas comerciales y de ocio, según la hora.
- **Ropa tendida, macetas y antenas** según estilo y riqueza.
- **Árboles:** 8–12 especies por bioma, con tamaño según la edad (crecen con los años) y balanceo por viento en el shader.
- **Vida de parques:** niños jugando, gente con perros, corredores y picnics.
- **Colas** en las paradas de bus y **multitudes** en los eventos.

### 6.8 Ciudadanos con nombre e historia

- Cada ciudadano tiene **nombre, edad, familia, casa, trabajo o colegio** y la **ruta actual**.
- Los nombres no se guardan: se generan de forma determinista a partir de su id, la semilla y las listas de nombres del estilo (ver [16.3](#163-técnicas-de-compresión)).
- **Seguir con la cámara** a cualquier ciudadano o vehículo.
- **Feed "Voces de la ciudad":** mensajes cortos generados por plantillas que **explican causas**. Tocar un mensaje lleva al lugar. Ejemplos:
  - *"Llevo 40 minutos en el atasco de la Av. del Puerto 😤"*
  - *"¡Por fin metro en Las Huertas!"*
  - *"En mi calle no recogen la basura desde el martes."*

### 6.9 Datos de estilos y piezas (ejemplo de JSON)

```json
{
  "style": "mediterraneo",
  "palette": "pal_mediterraneo",
  "roofs": ["teja_2aguas", "teja_4aguas", "azotea"],
  "pieces": {
    "ground_floor": ["portal_madera_01", "local_toldo_01", "local_toldo_02", "garaje_01"],
    "floor_bay":    ["ventana_persiana_01", "balcon_forja_01", "balcon_forja_02", "ventana_simple_01"],
    "corner":       ["chaflan_01", "esquina_balcon_01"],
    "accessories":  ["maceta_01", "aire_acond_01", "antena_01", "ropa_tendida_01"]
  },
  "rules": {
    "R": { "baja": {"floors": [1, 2]}, "media": {"floors": [3, 6]}, "alta": {"floors": [7, 14]} },
    "balcony_prob_by_level": [0.1, 0.3, 0.5, 0.6, 0.7],
    "accessory_density_by_wealth": [0.6, 0.4, 0.25]
  }
}
```

Añadir un estilo nuevo es **añadir datos y piezas**, no programar. Esto permite que Haiku genere borradores de estilos y que en el futuro haya mods.

---

## 7. Animaciones baratas: VAT y shaders

### 7.1 Por qué no se usa la animación "normal"

Las animaciones con esqueleto (`SkinnedMeshRenderer`) calculan huesos en la CPU, o en la GPU con un coste por personaje, y cada personaje es un *draw call* casi independiente. Con 3.000 peatones en un móvil es inviable.

### 7.2 Vertex Animation Textures (VAT)

**Idea:** la animación se **hornea en una textura**. Cada columna es un vértice y cada fila un fotograma; el color es la posición (y otra textura guarda las normales). En el juego, el *vertex shader* lee la textura y mueve los vértices. **Todos los peatones se dibujan con instancing en muy pocos draw calls.**

**Pipeline:**
1. Animaciones base: Mixamo (andar, correr, pedalear, sentarse, saludar, esperar, hablar por el móvil) o animaciones CC0. Ver la política de licencias en [13.7](#137-política-de-licencias-de-assets).
2. Script de Blender en modo `--background`:
   - Aplica la animación al cuerpo low-poly.
   - Muestrea N fotogramas por clip.
   - Escribe la textura de posiciones (RGBAHalf) y la de normales (RGBA8 octaédrica).
   - Escribe un JSON con los rangos de cada clip.
3. Importador de Unity (Editor): crea el material, el asset de datos de clips y la configuración de instancing.

**Datos por instancia:** clip, desfase de tiempo, velocidad de reproducción y 3 tintes (ropa superior, inferior, piel o pelo).

**Memoria estimada:**

| Elemento | Cálculo | Tamaño |
|---|---|---|
| Cuerpo adulto (≈ 400 vértices) × 8 clips × 30 fotogramas | 400 × 240 texeles × 8 B | ≈ 0,75 MB |
| Normales | 400 × 240 × 4 B | ≈ 0,38 MB |
| 4 cuerpos (adulto, mayor, niño, ciclista) | × 4 | **≈ 4,5 MB en total** |

**Variedad:**
- 4 cuerpos base × tintes por instancia (paletas de ropa por estilo y estación) × accesorios rígidos opcionales (sombrero, mochila, paraguas cuando llueve) que siguen el hueso de la cabeza o del torso.
- La posición de esos huesos se hornea en una textura aparte de 1–2 vértices por fotograma.

### 7.3 Vehículos

- Son **sólidos rígidos:** solo se mueve la transformación, sin animación.
- **Ruedas:** los vértices de las ruedas están marcados en el color de vértice y el shader los rota según la velocidad (dato por instancia).
- **Luces:** freno, intermitentes, faros y sirenas son emisivas mediante bits por instancia.
- **Variedad:** unos 40 modelos (turismos, furgonetas, camiones, autobuses, tranvías, servicios) con tintes por instancia (colores realistas ponderados).

### 7.4 Animación procedural en el shader

| Elemento | Técnica |
|---|---|
| Árboles y arbustos | Balanceo por altura (seno + ruido) según el viento global |
| Agua | Normales que se desplazan, espuma en las orillas por profundidad |
| Banderas y toldos | Ondulación con máscara en el color de vértice |
| Aerogeneradores, norias, grúas | Rotación en el shader alrededor de un pivote por instancia |
| Semáforos | Índice de color por instancia que actualiza la simulación |
| Ventanas y farolas | Emisión según la hora del día |
| Humo, vapor, fuego | Flipbooks en `Texture2DArray` sobre billboards instanciados |
| Lluvia y nieve | Partículas GPU (VFX Graph en PC; partículas simples en móvil) |

### 7.5 Nivel de detalle de la animación

| Distancia | Peatón | Vehículo |
|---|---|---|
| Cerca (< 80 m) | VAT a 30 fps, accesorios | Malla LOD0, ruedas y luces |
| Media (80–250 m) | VAT a 10 fps, sin accesorios | LOD1, solo luces |
| Lejos (250–600 m) | Billboard con 2 fotogramas | LOD2 / caja coloreada |
| Muy lejos (> 600 m) | No se dibuja (sí se simula) | Punto o línea de luz de noche |

### 7.6 Coste previsto

Con 3.000 peatones y 3.000 vehículos visibles en la gama media:
- **Draw calls:** < 40 para todos los agentes, gracias al instancing por tipo de malla y LOD.
- **Coste en CPU:** solo subir los buffers de instancias (posición, rotación y datos), unos 0,5 ms con Burst.

---

## 8. Simulación: el núcleo

### 8.1 Arquitectura de la simulación

- **`Sim.Core`:** librería **C# pura** (netstandard2.1) **sin dependencias de Unity**. Se compila y se testea en la nube con `dotnet test`. Unity la consume como ensamblado.
- **Tick fijo:** **10 ticks por segundo de juego a ×1.** A ×2 y ×4 se ejecutan más ticks por fotograma. Si el dispositivo no da abasto, la velocidad efectiva baja con suavidad y se avisa en el HUD (nunca se rompe la lógica).
- **Datos orientados a datos:**
  - Cada entidad (ciudadano, vehículo, edificio, segmento) es un **struct** dentro de arrays contiguos, con un **id estable** (índice + generación).
  - Sin objetos por ciudadano ni `GameObject` por agente.
  - Sin asignaciones de memoria en los bucles calientes.
- **Separación simulación y render:**
  - La simulación produce un **snapshot** compacto por tick (posiciones de los agentes visibles, cambios de edificios, colores de semáforos).
  - Unity interpola entre snapshots para dibujar a 30 o 60 fps aunque la simulación vaya a 10 Hz.
- **Orden de los sistemas en cada tick:**

| # | Sistema | Frecuencia | Notas |
|---|---|---|---|
| 1 | Aplicar comandos del jugador | Cada tick | Validados (ver [5.14](#514-implementación-técnica)) |
| 2 | Reloj, calendario y eventos | Cada tick | |
| 3 | Planificador de viajes (rutinas) | Cada tick, por lotes | Decide quién sale ahora |
| 4 | Cola de pathfinding | Cada tick, con presupuesto en ms | Ver [8.5](#85-pathfinding) |
| 5 | Movimiento de vehículos | Cada tick | IDM + intersecciones |
| 6 | Movimiento de peatones y pasajeros | Cada tick | Más simple que los vehículos |
| 7 | Transporte público | Cada tick | Embarques y desembarques |
| 8 | Despacho de servicios | Cada 5 ticks | Asigna vehículos a peticiones |
| 9 | Edificios: consumo, producción, problemas | Repartido: 1/20 de los edificios por tick | |
| 10 | Redes (agua, electricidad) | Cada 50 ticks o al cambiar la red | |
| 11 | Campos de rejilla | Cada 100 ticks, repartido por bandas | Ver [8.11](#811-campos-de-rejilla) |
| 12 | Demanda RCI y crecimiento | Cada 50 ticks | |
| 13 | Economía | Diaria (contable) y semanal (informe) | |
| 14 | Ciudadanos: envejecimiento, educación, migración | Diaria, repartida | |
| 15 | Estadísticas e historial | Cada hora de juego | Reducción de resolución con el tiempo |
| 16 | Notificaciones y cadenas de causas | Cada 10 ticks | |

### 8.2 Escalas de tiempo

Como en todos los city builders, **el tiempo está comprimido de forma distinta según el sistema**:
- Los vehículos se mueven a velocidades verosímiles en tiempo de juego ×1.
- Los días pasan mucho más rápido: 1 día ≈ 6 minutos reales.

Un trayecto de 15 minutos "simulados" dura unos segundos reales, y eso está bien. Lo importante es que las **relaciones** sean coherentes: un atasco de verdad retrasa al camión de bomberos lo bastante como para que el fuego se propague.

Todas las constantes (ritmo de propagación del fuego, tiempo de tolerancia a la basura, etc.) se calibran en el **tiempo de la simulación**, no en el reloj del calendario, y viven en JSON para balancearlas.

### 8.3 Población virtual y agentes físicos

- **Ciudadano (struct ≈ 16 bytes):**

```
uint32  householdId
uint32  workOrSchoolBuildingId
uint16  flags (edad, educación, salud, tiene coche, preferencia de transporte...)
uint8   happiness
uint8   wellbeing
uint32  currentTripId (0 = en un edificio)
```

- **Hogar:** id del edificio, miembros (rango de índices), riqueza y coche(s).
- **Viajes:** cuando un ciudadano debe moverse (rutina), se crea un **viaje** con origen, destino, modo y hora de salida. Al salir se pide una ruta.
- **Agentes físicos solo para viajes activos:** un viaje genera un vehículo, un peatón o un pasajero de transporte. El resto de la población **no existe físicamente**: es una fila en un array.
- **Tope de agentes físicos por gama** (ver [15.1](#151-gamas-de-dispositivo)). Si se supera:
  - Los viajes extra se simulan **de forma abstracta**: se calcula su duración con el coste de la ruta y se "teletransportan" al llegar.
  - **Sí suman carga** a los segmentos por los que pasan, mediante un **flujo estadístico** por segmento.
  - Es el **tráfico híbrido**: los atascos y sus consecuencias se mantienen aunque no se dibujen todos los coches.
  - Así una ciudad de 300.000 habitantes no necesita 300.000 agentes.
- **Coherencia visual:** los agentes físicos se priorizan **cerca de la cámara** y en los **vehículos de servicio** (siempre físicos, porque son los que el jugador sigue).

### 8.4 Red viaria: estructuras de datos

```
Node      { position, elevation, connectedSegments[≤8], junctionType, controlId }
Segment   { nodeA, nodeB, bezier(p0,p1,p2,p3), roadTypeId, length, lanesRange, flags }
Lane      { segmentId, index, direction, laneType (coche/bus/bici/tranvía/peatón),
            speedLimit, offset, length, vehiclesListHead }
LaneConn  { fromLane, toLane, junctionNode, conflictMask, turnType }
```

- **Grafo de carriles:** nodos = carriles; aristas = continuar por el mismo carril o pasar por una conexión de intersección.
- El **grafo de segmentos** (más pequeño) se usa para planificar la ruta y luego se resuelve el carril.
- **Índice espacial** de segmentos y carriles para consultas de proximidad.
- **Época de la red:** contador que sube con cada cambio. Invalida cachés de rutas y *landmarks* de forma selectiva.

### 8.5 Pathfinding

- **Algoritmo:** **A\*** sobre el grafo de segmentos (y de intersecciones con giros), con heurística **ALT** (*A\*, Landmarks, Triangle inequality*):
  - Se precomputan distancias desde 8–16 puntos de referencia.
  - Se recalculan por partes tras los cambios de red.
  - Mucho más rápido que la distancia euclídea en ciudades con ríos y autopistas.
- **Coste de un tramo:**

```
coste = longitud / velocidad_efectiva
      + penalización_giro (izquierda > derecha; cambio de sentido muy alto)
      + penalización_cruce (semáforo, stop)
      + peaje/tasa de congestión (según política y tipo de vehículo)
velocidad_efectiva = velocidad_límite × f(congestión_tramo)   // congestión medida, suavizada
```

  - La congestión de cada tramo se mide y se **suaviza** (media móvil). Se actualiza cada N ticks para evitar oscilaciones: que todos cambien de ruta a la vez.
  - **Aleatoriedad por conductor** (±10 % en el coste percibido), para que no todos elijan exactamente la misma ruta.
- **Restricciones por vehículo:**
  - Camiones prohibidos en distritos con política.
  - Carril bus solo para bus, taxi y emergencias.
  - Calles peatonales solo para servicios.
- **Cola repartida en el tiempo:** las peticiones van a una cola con prioridades (emergencias > servicios > transporte público > particulares). Se procesan con un **presupuesto de milisegundos por fotograma** según la gama. Si la cola crece, los viajes particulares esperan un poco más para salir (nunca se bloquea la simulación).
- **Caché de rutas:** por (origen, destino, tipo de vehículo, época de congestión cuantizada). Funciona muy bien con rutinas repetitivas (casa → trabajo).
- **Rutas jerárquicas** (ciudades grandes): la red se agrupa en celdas o distritos, con un grafo de "puertas" precomputado. Los viajes largos usan primero el grafo grueso y luego refinan los extremos.
- **Peatones y multimodal:**
  - Grafo de peatones: aceras, pasos, caminos, parques, escaleras.
  - Grafo de transporte público: paradas y líneas con frecuencia.
  - La ruta multimodal se calcula con **coste generalizado**: tiempo andando × 1,5 + espera media + tiempo a bordo + transbordo × penalización.
- **Re-ruta:** si un vehículo lleva X segundos parado o su ruta se ha roto (demolición), se pide una ruta nueva desde su posición.

### 8.6 Movimiento y comportamiento de conducción

- **Modelo de seguimiento:** **IDM (Intelligent Driver Model)** simplificado:

```
a = a_max · [ 1 − (v / v0)^4 − (s* / s)^2 ]
s* = s0 + v·T + (v · Δv) / (2 · √(a_max · b))
```

  `v0` = velocidad deseada (límite × factor del conductor), `s` = hueco con el vehículo de delante, `T` = tiempo de reacción, `s0` = distancia mínima, `b` = frenada cómoda. Hay parámetros por tipo de vehículo (camión: menos aceleración y más hueco).

- **Líder:** el vehículo de delante en el carril. Si el carril termina, se mira el primer vehículo del carril de salida previsto (*lookahead*).
- **Elección de carril con antelación:** al calcular la ruta se conoce el giro de cada intersección. El vehículo se coloca en un carril válido **con 1–2 segmentos de antelación**. Es la gran mejora frente a la IA de carriles de Cities: Skylines sin mods.
- **Cambio de carril:** solo en puntos de decisión y con **aceptación de hueco** (MOBIL simplificado: solo si mejora y no obliga a frenar fuerte al de detrás).
- **Intersecciones:**
  - Cada conexión de carril tiene una **máscara de conflictos** con las demás.
  - **Reserva:** un vehículo entra si las conexiones en conflicto están libres o si tiene prioridad (semáforo en verde, vía principal).
  - **Anti-bloqueo:** no se entra en la intersección si no hay hueco a la salida (evita "taponar el cruce").
- **Semáforos:**
  - Fases por grupos de conexiones.
  - Modos: automático, tiempos fijos o **adaptativo**. El adaptativo alarga la fase con más cola; se desbloquea con el Centro de control de tráfico.
- **Emergencias:** con sirena, los vehículos de delante se apartan si hay carril y la emergencia puede saltarse los semáforos en rojo con precaución.
- **Autobuses y tranvías:** se detienen en las paradas; con carril propio no sufren el tráfico.
- **Aparcamiento:**
  - Cada edificio tiene **plazas** según tipo y nivel; los aparcamientos y la calle añaden más.
  - Al llegar, el coche aparca en el destino. Si está lleno, **busca** en un radio creciente, lo que suma tráfico de búsqueda.
  - Si tras X intentos no encuentra, aparca lejos y termina andando, y la felicidad baja.

### 8.7 Atascos y "flujo de tráfico %"

- **Flujo de tráfico % de la ciudad:** media ponderada por distancia recorrida de (velocidad real / velocidad libre). Se muestra en el HUD de tráfico y en las estadísticas.
- **Atascos emergentes:** no se programan. Surgen de la capacidad de los carriles, los cruces, los giros a la izquierda sin carril propio, los aparcamientos llenos y los eventos.
- **Bloqueo total (*gridlock*):**
  - Detección: ciclo de vehículos que se esperan mutuamente más de X segundos en una intersección.
  - Resolución por orden:
    1. Permitir un "paso forzado" del vehículo con más espera.
    2. Re-ruta.
    3. Si la opción de partida lo permite, desaparición tras el tiempo configurado (ver [3.2](#32-opciones-de-partida-dificultad-a-medida)).
- **Comunicación:** los tramos con flujo < 40 % mantenido generan una notificación con el **origen y destino dominantes** de su tráfico (ver [9](#9-tráfico-herramientas-y-diagnóstico)).

### 8.8 Cadena de causa-efecto

Cada problema de un edificio guarda un **registro de causa** compacto:

```
CauseRecord {
  problem: FIRE_SPREAD
  cause: SERVICE_LATE
  serviceType: FIRE
  vehicleId: 1832
  expectedMinutes: 4, actualMinutes: 13
  blockingSegmentId: 5521   // el tramo donde más tiempo perdió
}
```

La interfaz convierte el registro en una frase y en botones de "ir a":

> **Calle Olivo 14 — Incendio propagado**
> Los bomberos tardaron **13 min** (lo esperado: 4 min).
> Perdieron **8 min** en el **cruce Av. del Puerto / C. Mayor** → [Ir al cruce] [Ver ruta del camión]

**Tres ejemplos completos:**

1. **Basura → enfermedad → abandono:**
   - Un vertedero lleno hace que los camiones tengan que ir a otro más lejano.
   - Las rutas se alargan y la basura se acumula.
   - Bajan la salud y la felicidad.
   - El edificio se abandona.
   - *La cadena muestra:* vertedero lleno → ruta media +11 min → 230 edificios con basura.
2. **Comercio sin mercancías:**
   - La salida de la autopista está saturada y los camiones de importación llegan tarde.
   - Las tiendas se quedan sin existencias, pierden clientes y cierran.
   - *La cadena muestra:* el enlace de la autopista como cuello de botella y el porcentaje de camiones que pasan por él.
3. **Industria sin trabajadores:**
   - Los trabajadores tardan más de 60 minutos en llegar.
   - Algunos renuncian y el puesto queda vacante.
   - Baja la producción, faltan mercancías y suben las importaciones.
   - *La cadena muestra:* el tiempo medio de los desplazamientos al trabajo y los modos usados, y sugiere "una línea de bus desde el barrio X reduciría el trayecto un 35 %".

### 8.9 Servicios con despacho

- **Peticiones:** los edificios generan peticiones (basura acumulada, enfermo, fallecido, incendio, delito) con prioridad y momento de creación.
- **Despachador por servicio:**
  - Asigna el vehículo libre con **menor coste de ruta real**, no el más cercano en línea recta.
  - Los camiones de basura tienen capacidad (20 t) y **encadenan recogidas** cercanas.
  - Las ambulancias van de una en una.
- **Isócronas de cobertura:**
  - Por cada base se calcula un **Dijkstra** limitado por tiempo con los costes actuales de congestión.
  - Se reparte en el tiempo y se refresca cada pocas horas de juego.
  - Es lo que muestra la capa de cobertura y lo que usa el crecimiento ("¿este edificio está cubierto?").
- **Presupuesto:** el deslizador del 50 % al 150 % cambia el número de vehículos activos y la eficiencia.

### 8.10 Redes de suministro

- **Bajo las carreteras de forma automática** (como en Cities: Skylines II): cada carretera lleva agua, alcantarillado y electricidad. Solo hacen falta **líneas manuales** para conexiones largas sin carretera (torres de alta tensión, tuberías troncales).
- **Modelo:**
  - Cada red se divide en **componentes conexas** (BFS o *union-find* incremental).
  - En cada componente: producción frente a consumo.
  - Si hay déficit, se reparte por prioridad (servicios esenciales primero) y por distancia a la fuente.
- **Electricidad:** curva de demanda día y noche; la solar sigue al sol, la eólica al viento y las baterías almacenan.
- **Agua:** calidad (contaminación en la toma). El agua contaminada causa enfermedades con retardo.
- **Alcantarillado:** si la capacidad de la salida o de la depuradora es insuficiente, hay atascos de alcantarillado y problemas en los edificios.

### 8.11 Campos de rejilla

| Campo | Resolución | Fuente | Dinámica |
|---|---|---|---|
| Contaminación del aire | 32 m | Industria, tráfico, centrales | Advección por viento + difusión + decaimiento |
| Contaminación del suelo | 32 m | Industria, vertederos | Acumulación lenta, limpieza lenta |
| Contaminación del agua | Por celda de agua | Desagües, industria costera | Transporte por corriente |
| Ruido | 16 m | Volumen de tráfico por tramo, industria, ocio, obras | Atenuación por distancia; recalculado |
| Valor del suelo | 16 m | Servicios, parques, vistas al agua, ruido, contaminación, transporte | Combinación ponderada, suavizada |
| Crimen | 32 m | Paro, educación, cobertura policial | |
| Riesgo de incendio | Por edificio | Edad, nivel, políticas | |
| Recursos naturales | 32 m | Mapa | Se agotan (petróleo, mineral) o se regeneran (bosque) |

- **Cómputo:** jobs de Burst en bandas (1/8 del mapa por pasada), con doble búfer para que la interfaz y la simulación lean un estado consistente.
- **Memoria:** mapa de 6×6 km a 32 m = 188×188 = 35.000 celdas por campo; a 16 m, 140.000 celdas. Con 1–2 bytes por celda, **menos de 2 MB para todos los campos**.

### 8.12 Demanda RCI y crecimiento

```
demanda_R = f(empleos libres, atractivo ciudad, impuestos R, felicidad media, vivienda vacía)
demanda_C = f(población residente, poder adquisitivo, turistas, mercancías disponibles, impuestos C)
demanda_I = f(demanda de mercancías local + exportación, trabajadores poco cualificados disponibles, impuestos I)
demanda_O = f(trabajadores cualificados disponibles, impuestos O, nivel educativo)
```

- Cada demanda va de −100 a +100 y se muestra en barras.
- El **crecimiento** elige parcelas vacías o mejorables según demanda × valor del suelo × servicios, con un límite de edificios nuevos por tick (para que la ciudad crezca de forma orgánica y repartida en el tiempo).

### 8.13 Economía (fórmulas base)

```
impuesto_edificio_semana = base_zona[densidad][nivel] × (tasa_impuesto / 9%) × ocupación
mantenimiento_servicio_semana = base_servicio × (presupuesto% / 100%) × multiplicador_dificultad
```

Todas las bases están en JSON. Se balancean con simulaciones automáticas (ver [8.16](#816-balanceo-automático-en-la-nube)).

### 8.14 Determinismo y reproducibilidad

- **Generador aleatorio propio** con semilla (PCG32 o xoshiro). Nunca se usa `System.Random` ni `UnityEngine.Random` dentro de `Sim.Core`.
- **Orden de iteración estable** (por id) y nada de diccionarios sin orden en la lógica.
- **Alcance:**
  - Determinismo garantizado **en el mismo dispositivo y la misma build**, suficiente para las pruebas de repetición y la depuración.
  - **No se exige entre plataformas** (no hay multijugador). Los elementos que se regeneran desde semillas (edificios, nombres) usan solo enteros, así que son idénticos en todas partes.
- **Hash de estado:** función `SimState.ComputeHash()` para comparar estados en los tests.

### 8.15 Presupuestos de la simulación

| Gama | ms de simulación por fotograma (a ×1) | Vehículos físicos | Peatones físicos | Rutas por fotograma | Población recomendada |
|---|---|---|---|---|---|
| Baja | ≤ 6 ms | 1.200 | 1.200 | ~60 | 40.000 |
| Media | ≤ 8 ms | 3.000 | 3.000 | ~150 | 100.000 |
| Alta | ≤ 8 ms | 6.000 | 6.000 | ~300 | 200.000 |
| PC | ≤ 10 ms | 16.000 | 16.000 | ~800 | 300.000+ |

Con poblaciones mayores a las recomendadas el juego sigue funcionando: sube la proporción de viajes abstractos y puede bajar la velocidad máxima de simulación.

### 8.16 Balanceo automático en la nube

- **Ciudades bot:** un ejecutable de consola (`Sim.Runner`) carga una ciudad de prueba o la genera con un *planner* simple (carreteras en cuadrícula, zonas y servicios según reglas). Simula N años de juego sin gráficos y escribe métricas en CSV: dinero, población, flujo de tráfico, problemas.
- **Uso:**
  - Detectar economías rotas (siempre en quiebra o dinero infinito).
  - Calibrar constantes.
  - Medir el rendimiento de `Sim.Core` en cada commit (regresiones).
- **Visor de depuración en PNG:** `Sim.Runner --render out.png` dibuja carreteras, edificios, vehículos y mapas de calor en una imagen 2D. Permite "ver" la simulación desde la nube sin Unity.

---

## 9. Tráfico: herramientas y diagnóstico

El tráfico es el corazón del género. El jugador debe poder **entender** y **arreglar** cada atasco.

| Herramienta | Qué hace |
|---|---|
| **Capa de tráfico** | Colorea cada tramo según el flujo (verde → amarillo → rojo → granate) y muestra el grosor según el volumen |
| **Inspector de tramo** | Volumen por hora, velocidad media, flujo %, % de camiones, top 5 orígenes y destinos (por distrito), gráfico de 24 h |
| **Explorador de rutas** | Selecciona un tramo, un edificio o un distrito y dibuja **todas las rutas que pasan por ahí**, coloreadas por tipo de vehículo. Es la herramienta clave para entender "de dónde vienen los coches" |
| **Editor de intersecciones** | Flechas de carril, conexiones, prioridades, semáforos (fases y tiempos), prohibiciones de giro, pasos de peatones |
| **Restricciones por tramo** | Límite de velocidad, prohibir camiones, solo bus, solo servicios, sentido único temporal |
| **Asesor de tráfico** | Analiza los peores 5 cruces y propone soluciones razonadas: *"El 42 % del tráfico de este cruce gira a la izquierda sin carril propio → prueba una rotonda o un carril de giro"*. Las sugerencias se aplican en modo plano para previsualizarlas |
| **Repetición de 24 h** | Reproduce el mapa de tráfico de las últimas 24 h de juego a cámara rápida (historial de baja resolución) para ver las horas punta |
| **Estadísticas de movilidad** | Reparto modal (coche, bus, metro, bici, a pie), tiempo medio de desplazamiento, flujo % histórico |

**Políticas y herramientas de demanda** (no solo de oferta): tasa de congestión, zona de bajas emisiones, aparcamientos disuasorios, transporte gratis, fomento de la bici y entregas nocturnas. El objetivo es que se pueda "ganar" al tráfico con urbanismo y transporte público, no solo con más carriles.

---

## 10. Mapas de calor y capas de información

### 10.1 Lista completa de capas

| # | Capa | Qué muestra | Fuente | Escala |
|---|---|---|---|---|
| 1 | Tráfico | Flujo % y volumen por tramo | Simulación de vehículos | Verde → granate |
| 2 | Transporte público | Líneas, ocupación, paradas y cobertura a 5 min andando | Transporte | Por línea |
| 3 | Ruido | Nivel de ruido | Campo de ruido | Azul → rojo |
| 4 | Contaminación del aire | Concentración | Campo del aire | Transparente → marrón |
| 5 | Contaminación del suelo | Concentración | Campo del suelo | Transparente → ocre |
| 6 | Contaminación del agua | Calidad del agua | Campo del agua | Azul → verde turbio |
| 7 | Valor del suelo | Valor | Campo | Rojo → verde |
| 8 | Felicidad | Media por edificio | Ciudadanos | Rojo → verde |
| 9 | Salud | Cobertura (isócrona) + nivel de salud | Despacho + ciudadanos | |
| 10 | Educación | Cobertura por nivel + nivel medio | Educación | |
| 11 | Crimen | Tasa + cobertura policial | Campo + despacho | |
| 12 | Bomberos | Cobertura + riesgo de incendio | Despacho + edificios | |
| 13 | Basura | Acumulación por edificio + rutas | Edificios + despacho | |
| 14 | Muerte | Cobertura de fúnebres y capacidad de cementerios | Despacho | |
| 15 | Electricidad | Red, producción frente a consumo, apagones | Redes | |
| 16 | Agua y alcantarillado | Red, calidad, capacidad | Redes | |
| 17 | Viento | Dirección e intensidad | Clima | Flechas animadas |
| 18 | Recursos naturales | Fértil, bosque, petróleo, mineral | Mapa | Iconos y colores |
| 19 | Densidad de población | Habitantes por celda | Ciudadanos | |
| 20 | Empleo | Puestos libres y paro por zona | Ciudadanos + edificios | |
| 21 | Turismo | Atractivo y flujos de turistas | Turismo | |
| 22 | Niveles de edificio | Nivel 1–5 por edificio | Edificios | |
| 23 | Distritos | Áreas, nombres, políticas | Distritos | Colores por distrito |
| 24 | Rutas | Explorador de rutas (ver [9](#9-tráfico-herramientas-y-diagnóstico)) | Pathfinding | Por tipo |
| 25 | Aparcamiento | Ocupación y búsquedas fallidas | Aparcamiento | |

### 10.2 Implementación barata

- **Campos de rejilla → textura:**
  - Cada campo se copia a una textura `R8` o `R16` del tamaño de la rejilla (máximo 375×375 a 16 m en 6 km).
  - Un **shader superpuesto** al terreno la muestrea con interpolación bilineal y la colorea con una rampa (textura 256×1).
  - **Coste:** una textura pequeña y un pase de shader.
- **Actualización por trozos:** solo se suben las bandas que han cambiado. En la gama baja, la capa se refresca cada 2 s; en PC, cada 0,25 s.
- **Capas por tramo** (tráfico, transporte): el color va en los **datos por instancia o por vértice** de la malla de carretera (un byte por tramo); no hacen falta mallas nuevas.
- **Capas por edificio** (felicidad, basura, nivel): tinte por instancia de las piezas del edificio, con el resto de la ciudad en gris claro.
- **Leyenda** siempre visible con valores y unidades; al tocar un punto, aparece el valor exacto.
- **Comparar:** deslizador para mezclar la capa con la vista normal (0–100 %).
- **Historial:** las capas de tráfico y contaminación guardan un historial de baja resolución (cada hora de juego, 24 h) para la repetición.

---

## 11. Interfaz y experiencia de usuario

### 11.1 Principios

1. **El mapa es el protagonista:** la interfaz ocupa como mucho el 20 % de la pantalla en reposo.
2. **Pulgar primero:** objetivos táctiles de **≥ 48 dp** y acciones frecuentes en la mitad inferior.
3. **Todo dato lleva a su origen:** cada número o icono se puede tocar para ir al lugar o ver el desglose.
4. **Consistencia:** la misma acción hace lo mismo en todas partes. Toque = seleccionar, toque largo = información o demoler (configurable).
5. **Información progresiva:** resumen → detalle → experto (por ejemplo, el editor de carriles solo aparece cuando lo pides).

### 11.2 Distribución en el móvil (horizontal)

```
┌───────────────────────────────────────────────────────────────────────────────┐
│ 💰 1.204.300 € (+8.120/sem) │ 👥 48.210 │ 😊 78% │ ▮▮▯ R C I O │ 📅 12 mar A3 │ ⏸ ▶ ▶▶ ▶▶▶ │ ☰ │
├───────────────────────────────────────────────────────────────────────────────┤
│ 🔔3                                                                     🗺️ Capas│
│ (notificaciones                                                         📊 Stats│
│  plegables)                     [ MAPA 3D ]                             💬 Feed │
│                                                                         🏆 Hitos│
│                                                                          📐 Plano│
│                                                                              │
├───────────────────────────────────────────────────────────────────────────────┤
│ 🛣️  🏘️  🗺️  ⚡  💧  🗑️  🏥  🚒  🚓  🎓  🚌  🌳  🏛️  🏭  🌲  ⛰️  🧰 │  ↶ ↷ │ 🔨 │
└───────────────────────────────────────────────────────────────────────────────┘
```

- **Barra superior:** dinero y balance semanal, población, felicidad, demanda RCI y O, fecha, velocidad (pausa, ×1, ×2, ×4) y menú ☰.
- **Lado izquierdo:** notificaciones (máximo 3 visibles, agrupadas por tipo).
- **Lado derecho:** capas, estadísticas, feed, hitos y modo plano.
- **Barra inferior:** categorías de construcción (ver [12.7](#127-barra-de-construcción)), deshacer, rehacer y demoler.
- **Al abrir una categoría:** sube un panel con pestañas de subcategorías y tarjetas con miniatura, nombre, coste e indicadores (cobertura, capacidad).
- **Modo vertical (incluido en la 1.0, secundario):** el juego se diseña para horizontal, pero se puede jugar en vertical con una distribución adaptada:

```
┌──────────────────────────────┐
│ 💰 1,2M (+8k) 👥 48k 😊78% ☰ │  ← barra compacta (demanda y fecha al tocar)
│ ⏸ ▶ ▶▶ ▶▶▶        🔔3   ⋯   │  ← velocidad, avisos y menú "⋯" (capas, stats, feed, hitos, plano)
├──────────────────────────────┤
│                              │
│          [ MAPA 3D ]         │
│                              │
├──────────────────────────────┤
│ 🛣️ 🏘️ 🗺️ ⚡ 💧 🗑️ 🏥 🚒 🚓 │  ← categorías en 2 filas
│ 🎓 🚌 🌳 🏛️ 🏭 🌲 ⛰️ 🧰 ↶ ↷ │
└──────────────────────────────┘
```

  - Los paneles (categoría abierta, información, capas) **suben desde abajo** como hojas deslizables (medio o pantalla completa).
  - La rotación es automática y se puede bloquear en Ajustes (ver [12.4](#124-ajustes)).
  - Al rotar se conserva la cámara y la herramienta activa.

### 11.3 Gestos

| Gesto | Sin herramienta | Con herramienta activa |
|---|---|---|
| Toque | Seleccionar (edificio, vehículo, ciudadano, tramo) | Colocar el fantasma o el punto |
| Toque largo | Información rápida | Lupa de precisión |
| Arrastrar 1 dedo | Desplazar la cámara | Usar la herramienta (trazar, pintar) |
| Arrastrar 2 dedos | Desplazar la cámara | Desplazar la cámara |
| Pellizcar | Zoom | Zoom |
| Girar 2 dedos | Rotar la cámara | Rotar el objeto que se coloca |
| Deslizar 2 dedos en vertical | Inclinar la cámara | Inclinar la cámara |
| Doble toque | Centrar y acercar | — |

### 11.4 Cámara

- **Límites:** zoom de 15 m a 3 km de altura; inclinación de 25° a 85°.
- **Inercia suave,** sin rebotes bruscos.
- **Vista de ciudadano** (tercera persona siguiendo a un agente) y **modo foto:** sin interfaz, profundidad de campo en PC y gama alta, hora del día y clima ajustables.

### 11.5 Paneles de información

| Panel | Contenido |
|---|---|
| Edificio que crece | Nombre generado, zona, nivel y progreso al siguiente (con qué falta), residentes o trabajadores, servicios cubiertos ✓/✗, problemas con **cadena de causas**, consumo, impuestos |
| Edificio de servicio | Capacidad y uso, vehículos (en uso / total), presupuesto, mantenimiento, cobertura (mostrar isócrona), botón "mover" |
| Ciudadano | Nombre, edad, familia, educación, trabajo o colegio, felicidad (por qué), viaje actual (ruta), botón "seguir" |
| Vehículo | Tipo, propietario o servicio, origen → destino, ruta dibujada, tiempo perdido en atascos |
| Tramo o intersección | Ver [9](#9-tráfico-herramientas-y-diagnóstico) |
| Distrito | Población, felicidad, políticas, estilo, estadísticas |
| Línea de transporte | Paradas, vehículos, ocupación por tramo, pasajeros por semana, coste e ingresos |

### 11.6 Notificaciones y explicaciones

- **Tipos:** crítica (rojo, con vibración opcional), advertencia (ámbar), información (azul) y logro (dorado).
- **Agrupación:** "37 edificios sin recogida de basura" en lugar de 37 avisos.
- **Contenido:** toda notificación problemática incluye la **causa principal** y un botón **[Ir]** o **[Ver por qué]**.
- **Configuración:** silenciar por tipo y frecuencia máxima.

### 11.7 PC

- La misma interfaz escalada (UI Toolkit con escalado por DPI), con información al pasar el ratón y atajos visibles.
- Los paneles pueden quedarse abiertos a la vez.

| Atajo | Acción |
|---|---|
| Espacio | Pausa |
| 1 / 2 / 3 | Velocidades |
| F1–F12 | Capas favoritas |
| R | Herramienta de carreteras |
| Z | Zonas |
| B | Modo plano |
| Supr | Demoler |
| Esc | Cancelar / menú |
| Tab | Ocultar la interfaz |

### 11.8 Tecnología de la interfaz

- **UI Toolkit** (UXML + USS) para todo el HUD y los menús:
  - Estilos centralizados (temas), enlace de datos, rendimiento con listas largas (virtualización) y escalado sencillo para móvil y PC.
  - Unity 6 lo soporta en runtime de forma madura.
- **Marcadores en el mundo** (iconos de problemas sobre los edificios, números de línea): **sprites instanciados** en el render 3D, no elementos de la interfaz. Así 2.000 iconos no hunden el rendimiento.
- **uGUI solo si** hace falta algo muy concreto que UI Toolkit no cubra (no se espera).
- **Bocetos:** en Figma (MCP disponible), con componentes y variables de color. Se generan desde este documento y luego se pasan a UXML/USS.

### 11.9 Accesibilidad

- **Daltonismo:** 3 paletas alternativas para las capas (deuteranopía, protanopía, tritanopía) y patrones además de colores en las capas críticas.
- **Texto:** escala de la interfaz de 80 % a 150 % y fuente con alta legibilidad.
- **Movimiento:** reducción de vibraciones y animaciones, y opción de mantener pulsado en lugar de doble toque.
- **Subtítulos o iconos** para los avisos sonoros.

### 11.10 Localización

- **Lanzamiento:** español e inglés.
- **Después:** portugués, francés, alemán, italiano, polaco, turco, ruso, japonés, chino y coreano. Los textos van en tablas del paquete Localization y Haiku hace el primer borrador de traducción.
- **Datos generados:** los nombres de calles y ciudadanos usan listas por estilo y por idioma.

### 11.11 Tutorial y aprendizaje

- **Misiones de aprendizaje** opcionales en la primera partida:
  1. Conecta con la autopista.
  2. Zonifica.
  3. Agua y luz.
  4. Basura.
  5. Tu primer atasco: usa el inspector.
- **Consejos contextuales** la primera vez que aparece cada problema.
- **Biblioteca "¿Cómo funciona?"** dentro del juego: fichas breves con animaciones de cada sistema.
- Todo se puede saltar.

---

## 12. Menús completos y catálogo

### 12.1 Mapa de pantallas

```
Arranque (logo → carga de datos)
└── Menú principal
    ├── Continuar (última partida)
    ├── Nueva partida
    │   ├── Elegir mapa (miniatura, recursos, agua, dificultad del terreno)
    │   ├── Modo (Carrera / Sandbox / Diorama / Escenario)
    │   ├── Opciones de partida (ver 3.2)
    │   └── Nombre de la ciudad + estilo inicial → [Empezar]
    ├── Cargar partida (lista con miniatura, población, fecha, tamaño del archivo)
    │   └── Acciones: cargar · renombrar · duplicar · exportar · borrar
    ├── Escenarios (lista con objetivo y dificultad)
    ├── Editor de mapas (después del lanzamiento)
    ├── Estilos y mods (después del lanzamiento: mods de datos en móvil y PC)
    ├── Plantillas (gestionar .urbeplan: importar, exportar, borrar)
    ├── Logros
    ├── Ajustes
    ├── Créditos y licencias
    ├── Desbloquear juego completo (si se juega la demo)
    └── Salir (solo en PC)

En partida
├── HUD (11.2)
├── Barra de construcción (12.7)
├── Capas (10)
├── Estadísticas (población, economía, tráfico, servicios, transporte, educación, salud, contaminación, turismo)
├── Presupuesto e impuestos
├── Políticas (ciudad y por distrito)
├── Préstamos
├── Feed "Voces de la ciudad"
├── Hitos y desbloqueos
├── Modo plano
└── Menú de pausa (☰)
    ├── Continuar
    ├── Guardar / Guardar como
    ├── Cargar
    ├── Ajustes
    ├── Modo foto
    ├── Ayuda "¿Cómo funciona?"
    └── Salir al menú principal
```

### 12.2 Menú principal

- **Fondo:** la última ciudad guardada en vivo, con la cámara orbitando lentamente. Si no hay partida, una ciudad de demostración incluida en los datos.
- **Botón grande "Continuar"** con la miniatura y los datos de la última partida.
- **Indicador de almacenamiento:** "Partidas: 23 MB · Caché: 41 MB [Limpiar]".

### 12.3 Nueva partida

- **Mapas al lanzamiento:** 6 mapas con biomas (templado, mediterráneo, nórdico, tropical, desierto, montaña). Cada uno con río, mar o lago y distintas conexiones exteriores.
- **Semilla:** visible y editable para compartir.

### 12.4 Ajustes

| Sección | Opciones |
|---|---|
| **Gráficos** | Gama (Auto / Baja / Media / Alta / Personalizada) · Escala de resolución (50–100 %, dinámica sí o no) · FPS máximo (30 / 60 / 90 / 120 según pantalla; PC: libre) · Sombras (no / cerca / medias / lejanas) · Calidad de los árboles · Densidad de decoración · Máximo de vehículos y peatones visibles · Agua (simple / con reflejos) · Postproceso (no / ligero / completo) · Modo ahorro de batería |
| **Simulación** | Duración del día (3 / 6 / 12 min) · Velocidad máxima permitida · Autoguardado (no / 5 / 10 / 20 min) · Ranuras de autoguardado (1–5, por defecto 3) |
| **Controles** | Sensibilidad de la cámara · Invertir ejes · Desplazamiento del cursor (px) · Lupa sí o no · Confirmación ✓ sí o no · Modo una mano · Zurdo o diestro · Vibración · Construir en pausa · (PC) Reasignar atajos |
| **Interfaz** | Escala · Orientación (auto / horizontal / vertical) · Paleta para daltonismo · Idioma · Notificaciones por tipo · Mostrar nombres de calles · Mostrar iconos de problemas |
| **Audio** | General · Música · Ambiente · Efectos · Interfaz · Silenciar en segundo plano |
| **Almacenamiento** | Tamaño de cada partida · Compresión (rápida / máxima) · Borrar caché regenerable · Exportar o importar una partida (`.urbe`) · Borrar los autoguardados antiguos |
| **Cuenta y nube (opcional)** | Conectar Google Play Games o Steam · Sincronizar partidas sí o no · Sincronizar logros |
| **Privacidad** | Estadísticas de uso anónimas (apagado por defecto) · Informes de errores (preguntar / sí / no) |
| **Acerca de** | Versión · Créditos · Licencias de terceros · Restaurar compra |

### 12.5 Menú de pausa

Al abrirse, pausa la simulación. Ofrece guardado rápido con confirmación visual del tamaño ("Guardado · 3,2 MB").

### 12.6 HUD y paneles

Ver [11.2](#112-distribución-en-el-móvil-horizontal) y [11.5](#115-paneles-de-información).

### 12.7 Barra de construcción

| Icono | Categoría | Subcategorías (pestañas) |
|---|---|---|
| 🛣️ | Carreteras | Calles · Avenidas · Autopistas · Peatonal y bici · Intersecciones y rotondas · Enlaces · Aparcamientos · Herramientas (actualizar, cuadrícula, paralela, editor de cruces) |
| 🏘️ | Zonas | R baja / media / alta · C barrio / ocio / turismo · I genérica · O normal / tecnológica · Mixta · Borrar |
| 🗺️ | Distritos | Pintar · Borrar · Políticas · Estilos |
| ⚡ | Electricidad | Producción · Almacenamiento · Distribución |
| 💧 | Agua | Captación · Tratamiento · Almacenamiento · Alcantarillado |
| 🗑️ | Basura | Vertederos · Incineración · Reciclaje |
| 🏥 | Salud y muerte | Atención · Hospitales · Mayores · Cementerios |
| 🚒 | Bomberos | Estaciones · Prevención |
| 🚓 | Policía | Estaciones · Prisión |
| 🎓 | Educación | Infantil y primaria · Secundaria y FP · Universidad · Cultura |
| 🚌 | Transporte | Bus · Tranvía · Metro · Tren · Agua · Aire · Otros (taxi, bici, teleférico) |
| 🌳 | Parques y ocio | Parques · Plazas · Deportes · Ocio · Naturaleza |
| 🏛️ | Monumentos | Únicos · Emblemáticos · Maravillas |
| 🏭 | Industria especializada | Agricultura · Forestal · Petróleo · Minería · Fábricas · Logística |
| 🌲 | Decoración y paisajismo | Árboles · Vegetación · Mobiliario · Caminos · Vallas y muros · Agua decorativa |
| ⛰️ | Terreno | Subir · Bajar · Aplanar · Suavizar · Rampa · Agua (sandbox) |
| 🧰 | Herramientas | Mover · Copiar y pegar · Plantillas · Simetría · Demoler por área · Nombres de calles |

### 12.8 Catálogo de elementos colocables

> **Tamaño** en celdas de 8 m (ancho × fondo). **Hito** = número de la tabla de [3.3](#33-hitos-de-población). Los costes y mantenimientos viven en `Data/buildings/*.json` y se balancean en M5 (ver [PLAN_DE_TRABAJO.md](PLAN_DE_TRABAJO.md)).

#### Carreteras e intersecciones (prefabricados)

| Elemento | Tamaño | Función | Hito |
|---|---|---|---|
| Rotonda pequeña | 4×4 | Cruce de calles, 1 carril | 0 |
| Rotonda mediana | 6×6 | 2 carriles | 2 |
| Rotonda grande | 9×9 | Avenidas | 4 |
| Turbo-rotonda | 10×10 | Alta capacidad, carriles en espiral | 6 |
| Enlace en diamante | — | Autopista ↔ avenida | 0 |
| Enlace en trompeta | — | Autopista ↔ vía en T | 4 |
| Enlace en trébol | — | Autopista ↔ autopista | 6 |
| Glorieta partida | — | Autopista ↔ avenida, compacta | 5 |
| Enlace de turbina | — | Autopista ↔ autopista, alta capacidad | 8 |
| Aparcamiento en superficie | 3×4 | 60 plazas | 1 |
| Aparcamiento en altura | 3×3 | 300 plazas | 4 |
| Aparcamiento subterráneo con plaza | 4×4 | 250 plazas + plaza encima | 6 |
| Aparcamiento disuasorio (P+R) | 6×6 | 500 plazas junto a transporte | 6 |
| Gasolinera / estación de carga | 2×3 | Comercio de vía; recarga | 1 |
| Peaje | — | Cobra en autopistas | 5 |

#### Electricidad

| Elemento | Tamaño | Producción y función | Hito |
|---|---|---|---|
| Aerogenerador pequeño | 1×1 | 1 MW según el viento | 0 |
| Central de carbón | 6×6 | 40 MW, mucha contaminación | 0 |
| Placas solares sobre tejado (política) | — | +10 % de autoconsumo por distrito | 3 |
| Planta solar fotovoltaica | 6×8 | 12 MW de día | 4 |
| Parque eólico | 8×8 | 25 MW según el viento | 4 |
| Central de ciclo combinado (gas) | 6×6 | 60 MW, contaminación media | 6 |
| Planta termosolar | 10×10 | 30 MW, con almacenamiento nocturno parcial | 6 |
| Presa hidroeléctrica | Variable | Según caudal y altura | 5 |
| Planta geotérmica | 4×4 | 20 MW constantes (mapas con recurso) | 7 |
| Central nuclear | 12×12 | 250 MW, sin contaminación del aire, riesgo opcional | 8 |
| Planta de fusión | 10×10 | 600 MW (maravilla) | 10 |
| Batería de red | 3×3 | Almacena 50 MWh | 5 |
| Torre de alta tensión | 1×1 | Línea manual de larga distancia | 0 |
| Subestación | 2×2 | Amplía la capacidad local de la red | 3 |

#### Agua y alcantarillado

| Elemento | Tamaño | Función | Hito |
|---|---|---|---|
| Bombeo de agua de río | 2×2 | 60.000 m³/sem; calidad según la toma | 0 |
| Pozo de agua subterránea | 2×2 | 30.000 m³/sem; contaminable por el suelo | 1 |
| Torre de agua | 1×1 | Pequeña reserva y presión | 1 |
| Desagüe | 2×2 | Vierte aguas residuales (contamina) | 0 |
| Depuradora | 4×4 | Reduce un 85 % la contaminación del vertido | 3 |
| Depuradora avanzada | 6×6 | 99 %, reutilización parcial | 7 |
| Planta potabilizadora | 4×4 | Limpia el agua contaminada de la toma | 4 |
| Desaladora | 6×6 | Agua de mar, mucha electricidad | 8 |
| Tanque de tormentas | 3×3 | Evita inundaciones con lluvia intensa | 5 |

#### Basura

| Elemento | Tamaño | Función | Hito |
|---|---|---|---|
| Vertedero | 8×8 | Capacidad finita; se puede vaciar hacia la incineradora | 1 |
| Punto limpio | 2×2 | Reduce la basura del barrio | 3 |
| Estación de transferencia | 4×4 | Concentra la basura para camiones grandes | 5 |
| Planta de reciclaje | 6×6 | Convierte basura en materiales para la industria | 5 |
| Planta de compostaje | 6×6 | Basura orgánica → abono para agricultura | 5 |
| Incineradora | 6×6 | Elimina basura y genera electricidad; contamina | 6 |
| Planta de biogás | 4×4 | Gas a partir de residuos; algo de electricidad | 7 |

#### Salud y muerte

| Elemento | Tamaño | Función | Hito |
|---|---|---|---|
| Centro de salud | 2×3 | Atención básica, 2 ambulancias | 1 |
| Clínica | 3×4 | 60 camas, 5 ambulancias | 3 |
| Hospital | 6×6 | 300 camas, 12 ambulancias | 5 |
| Hospital universitario | 8×8 | 600 camas, mejora la salud media | 8 |
| Helipuerto médico | 3×3 | Ambulancias aéreas (ignoran el tráfico) | 7 |
| Centro de investigación médica | 4×4 | +esperanza de vida en la ciudad | 8 |
| Residencia de mayores | 4×4 | +salud y felicidad de los mayores | 4 |
| Centro de salud mental | 3×3 | +felicidad, −crimen | 6 |
| Cementerio | 8×8 | Capacidad finita, 6 coches fúnebres | 3 |
| Crematorio | 4×4 | Sin límite de capacidad, más lento | 5 |

#### Bomberos

| Elemento | Tamaño | Función | Hito |
|---|---|---|---|
| Parque de bomberos de barrio | 2×3 | 2 camiones | 2 |
| Parque de bomberos | 4×4 | 6 camiones | 3 |
| Base de helicópteros antiincendios | 5×5 | Incendios forestales y altos | 7 |
| Torre de vigilancia forestal | 1×1 | Detección temprana de incendios forestales | 4 |

#### Policía

| Elemento | Tamaño | Función | Hito |
|---|---|---|---|
| Puesto de policía de barrio | 2×2 | 2 patrullas | 2 |
| Comisaría | 4×4 | 8 patrullas, calabozo | 3 |
| Jefatura central | 6×6 | 20 patrullas, más eficacia en toda la ciudad | 6 |
| Prisión | 8×8 | Capacidad de reclusos; ruido y menos valor del suelo | 5 |
| Base de helicópteros policiales | 5×5 | Patrulla aérea | 8 |

#### Educación y cultura

| Elemento | Tamaño | Función | Hito |
|---|---|---|---|
| Guardería | 2×2 | Más natalidad y felicidad de las familias | 2 |
| Colegio de primaria | 4×4 | 300 alumnos | 2 |
| Instituto | 6×6 | 1.000 alumnos | 4 |
| Centro de formación profesional | 4×6 | 600 alumnos, empleo industrial cualificado | 5 |
| Universidad (campus modular) | 10×10 + edificios | 4.000 estudiantes; facultades añadibles | 6 |
| Biblioteca de barrio | 2×2 | +educación de los adultos | 3 |
| Biblioteca central | 4×4 | +educación, atrae visitantes | 6 |
| Museo de ciencia | 6×6 | +educación, turismo | 7 |
| Planetario | 4×4 | Turismo, educación | 8 |

#### Transporte público

| Elemento | Tamaño | Función | Hito |
|---|---|---|---|
| Parada de bus | En la acera | — | 2 |
| Cochera de autobuses | 4×6 | 20 autobuses | 2 |
| Intercambiador de autobuses | 6×6 | Terminal de varias líneas | 5 |
| Estación de autobuses interurbanos | 6×8 | Conexión con el exterior | 5 |
| Parada de tranvía | En la vía | — | 5 |
| Cochera de tranvías | 6×8 | 12 tranvías | 5 |
| Estación de metro | 3×3 (entrada) | Subterránea o elevada | 6 |
| Cochera de metro | 8×10 | 10 trenes | 6 |
| Estación de cercanías | 4×8 | Tren urbano y regional | 7 |
| Estación central de tren | 10×16 | Gran nudo con conexión exterior | 8 |
| Terminal de mercancías ferroviaria | 10×12 | Importación y exportación | 7 |
| Muelle de ferry | 3×4 | Línea por agua | 7 |
| Puerto de pasajeros | 8×10 | Cruceros y turistas | 8 |
| Puerto de carga | 12×16 | Mercancías por mar | 7 |
| Aeródromo regional | 16×24 | Turistas y negocios | 7 |
| Aeropuerto internacional | 30×40 (modular) | Gran turismo y carga aérea | 9 |
| Cochera de taxis | 3×4 | 20 taxis | 4 |
| Parada de taxi | En la acera | — | 4 |
| Estación de bicis compartidas | 1×2 | 20 bicis | 3 |
| Estación de teleférico | 2×3 | Mapas con relieve | 6 |

#### Parques y ocio

| Elemento | Tamaño | Función | Hito |
|---|---|---|---|
| Parque de barrio | 2×2 | +valor del suelo | 1 |
| Parque infantil | 2×2 | +felicidad de las familias | 1 |
| Plaza peatonal | 3×3 | Comercio y paseo | 2 |
| Plaza mayor con soportales | 6×6 | Comercio, eventos | 4 |
| Huertos urbanos | 3×3 | Felicidad, algo de comida | 3 |
| Parque para perros | 2×3 | Felicidad | 3 |
| Pista de skate | 2×3 | Felicidad de los jóvenes | 3 |
| Cancha de baloncesto | 2×3 | Felicidad, salud | 2 |
| Campo de fútbol | 6×8 | Salud, felicidad | 3 |
| Piscina municipal | 4×4 | Salud, felicidad en verano | 4 |
| Polideportivo | 6×6 | Salud | 5 |
| Parque grande | 8×8 | +valor del suelo en un radio amplio | 4 |
| Jardín botánico | 8×8 | Turismo, felicidad | 6 |
| Lago artificial con paseo | Variable | Valor del suelo, turismo | 5 |
| Paseo marítimo | Por tramo | Valor del suelo costero, turismo | 5 |
| Playa urbana | Por tramo | Turismo de verano | 5 |
| Mirador | 1×2 | Turismo | 3 |
| Zona de picnic | 2×2 | Felicidad | 2 |
| Auditorio al aire libre | 6×6 | Eventos de conciertos | 6 |
| Zoo | 12×12 | Turismo | 7 |
| Parque de atracciones | 14×14 | Turismo, ocio, tráfico fuerte | 8 |
| Recinto ferial y mercadillo | 8×8 | Eventos programables | 5 |
| Estadio | 12×14 | Partidos (grandes picos de tráfico) | 7 |

#### Monumentos, emblemáticos y maravillas

| Elemento | Tamaño | Función | Hito |
|---|---|---|---|
| Ayuntamiento | 4×4 | Desbloquea políticas por distrito | 2 |
| Centro de control de tráfico | 3×3 | Semáforos adaptativos y repetición de 24 h | 6 |
| Estación meteorológica | 2×2 | Previsión del tiempo y alertas | 4 |
| Oficina de turismo | 2×2 | +turistas | 5 |
| Centro de emergencias 112 | 3×3 | −10 % de tiempo de respuesta de todos los servicios | 6 |
| Torre del reloj | 2×2 | Felicidad, identidad | 3 |
| Catedral | 6×10 | Turismo, felicidad | 7 |
| Ópera | 6×8 | Turismo, cultura | 8 |
| Museo de arte | 6×6 | Turismo, cultura | 7 |
| Mercado central | 6×8 | Comercio, turismo | 5 |
| Estación histórica | 8×12 | Funciona como estación de tren, turismo | 8 |
| Faro | 2×2 | Costa, turismo | 4 |
| Puente emblemático | Especial | Tramo de autopista o avenida con diseño único | 8 |
| Torre de comunicaciones | 3×3 | Mirador, turismo | 8 |
| Rascacielos emblemático | 4×4 | Oficinas, turismo | 9 |
| Centro de congresos | 8×8 | Turismo de negocios | 8 |
| Observatorio | 3×3 | Educación, turismo (mejor en montaña) | 8 |
| Puerta monumental | 3×2 | Felicidad, turismo | 7 |
| Estadio olímpico | 16×18 | Grandes eventos | 9 |
| Arcología verde (maravilla) | 8×8 | Vivienda para 10.000 personas sin contaminación | 10 |

#### Industria especializada y logística

| Elemento | Tamaño | Función | Hito |
|---|---|---|---|
| Granja de cultivos | 6×6 | Materia prima agrícola (requiere suelo fértil) | 5 |
| Invernaderos | 4×6 | Agricultura intensiva | 6 |
| Granja ganadera | 6×8 | Materia prima agrícola | 5 |
| Silo y molino | 3×3 | Procesado agrícola | 5 |
| Explotación forestal | 6×6 | Madera (requiere bosque; se regenera) | 5 |
| Aserradero | 4×4 | Procesado de madera | 5 |
| Pozo de petróleo | 2×2 | Petróleo (se agota) | 5 |
| Refinería | 8×8 | Combustibles y plásticos | 6 |
| Mina | 6×6 | Mineral (se agota) | 5 |
| Planta de procesado de minerales | 6×6 | Metales | 6 |
| Fábrica de alimentos | 6×6 | Bienes de consumo | 6 |
| Fábrica de muebles | 6×6 | Bienes de consumo | 6 |
| Fábrica de electrónica | 6×6 | Bienes de alto valor | 7 |
| Almacén logístico | 6×8 | Almacena y reparte mercancías; reduce viajes largos | 5 |
| Centro de distribución | 8×10 | Gran nodo de camiones | 7 |

#### Decoración y paisajismo (lista abierta, ampliable por datos)

| Grupo | Elementos al lanzamiento |
|---|---|
| Árboles | Encina, pino piñonero, olivo, plátano de sombra, abedul, abeto, palmera, cerezo, roble, chopo, ciprés, jacarandá (por bioma) |
| Vegetación | Setos, arbustos, macizos de flores, césped, cañaverales |
| Mobiliario | Farolas (3 estilos), bancos (3), papeleras, fuentes de beber, bolardos, kioscos, quiosco de música, marquesinas, buzones, bicicleteros |
| Monumentos pequeños | Estatuas (6), fuentes ornamentales (4), obeliscos, esculturas modernas |
| Caminos | Peatonal, de tierra, entablado, escaleras, puente peatonal, carril bici, sendero de montaña |
| Vallas y muros | Valla de madera, metálica, muro de piedra, muro de contención, setos altos |
| Agua decorativa | Estanque, canal, cascada, muelle de madera, embarcadero |
| Otros | Pérgolas, toldos, zonas de picnic, áreas de juegos pequeñas, iluminación decorativa |

**Total al lanzamiento:** unos 190 elementos colocables, entre prefabricados de carreteras, servicios, transporte, ocio, monumentos, industria y unos 60 de decoración. Con las variantes por estilo de los servicios, más de 300 modelos.

### 12.9 Edificios que crecen (procedurales) por zona

| Zona y densidad | Tipologías generadas | Plantas |
|---|---|---|
| R baja | Unifamiliar aislada, pareada, adosados, casa con patio, casa de pueblo | 1–2 |
| R media | Bloque lineal, edificio entre medianeras, edificio con patio de manzana, casas de pisos | 3–6 |
| R alta | Torre residencial, bloque en altura con podio, torres gemelas | 7–40 |
| C barrio baja | Tienda de barrio, panadería, farmacia, ultramarinos, taller | 1–2 |
| C alta | Galería comercial, gran almacén, centro comercial urbano, torre comercial con podio | 3–20 |
| C ocio | Bares, restaurantes, cines, salas de conciertos, discotecas (activos de noche) | 1–5 |
| C turismo | Hoteles (de hostal a gran hotel), tiendas de recuerdos, restaurantes con terraza | 2–25 |
| I genérica | Naves, talleres, fábricas, almacenes, depósitos | 1–3 (+chimeneas) |
| O media | Edificios de oficinas bajos, sedes con jardín | 3–8 |
| O alta | Torres de oficinas, rascacielos | 10–60 |
| O tecnológica | Campus tecnológicos con jardín, laboratorios | 2–8 |
| Mixta | Bajos comerciales + viviendas encima, con soportales opcionales | 3–10 |

Cada tipología × 8 estilos × 5 niveles × 16 parcelas × semilla da miles de variantes reales.

---

## 13. Arte y dirección visual

### 13.1 Estilo

- **Low-poly estilizado:** facetado suave, siluetas legibles desde lejos y colores planos con degradados sutiles.
- **Una textura de paleta compartida** (256×256) con bloques de color y degradados. Los modelos mapean sus UV a esos bloques. Resultado: **un solo material** para casi todo y batching masivo.
- **Oclusión ambiental horneada** en el color de vértice, sin coste en tiempo real.
- **Iluminación:**
  - Una luz direccional (sol o luna) con color según la hora y luz ambiental por gradiente.
  - Sombras en tiempo real solo cerca de la cámara (y nada en la gama baja).
  - Ventanas y farolas emisivas en lugar de luces reales.
- **Postproceso:**
  - Tonemapping y corrección de color por hora del día.
  - Bloom ligero en gama alta y PC.
  - Niebla de distancia (también oculta el final del dibujado).

### 13.2 Presupuestos de polígonos (triángulos, LOD0)

| Tipo | LOD0 | LOD1 | LOD2 / impostor |
|---|---|---|---|
| Peatón | 300–500 | 150 | Billboard |
| Turismo | 300–600 | 150 | Caja de 12 triángulos |
| Autobús o camión | 600–1.000 | 250 | Caja |
| Pieza de edificio modular | 20–200 | Fusionada | — |
| Edificio que crece completo (suma de piezas) | 300–3.000 | 30 % | Impostor por manzana (lejos) |
| Servicio o monumento | 1.500–12.000 | 30 % | Impostor octaédrico |
| Árbol | 150–400 | 60 | Impostor (cruz o octaédrico) |
| Prop (banco, farola) | 20–150 | — | Desaparece |

### 13.3 Niveles de detalle e impostores

- **LOD por grupo** con transiciones suaves (*dither crossfade*) en gama media o superior.
- **Impostores octaédricos** horneados en el editor para árboles y edificios únicos.
- **Impostor de manzana** para vistas muy lejanas: se hornea en tiempo de carga y se actualiza cuando cambia la manzana. Va en la caché regenerable (ver [16.6](#166-caché-regenerable)).

### 13.4 Texturas y compresión

- Atlas de paleta por estilo (256×256 o 512×512), atlas de detalles (ventanas y rótulos, 1024×1024) y texturas VAT.
- **Android:** ASTC (6×6 para color, 4×4 para la interfaz). **PC:** BC7.
- Sin texturas de normales en los edificios: el low-poly no las necesita. Solo en el agua y el terreno.

### 13.5 Kit modular: reglas de autoría

- **Rejilla:**
  - Módulo de fachada de 3 m de ancho.
  - Altura de planta: 3 m (residencial) o 4 m (comercial y oficinas); planta baja de 4 m.
- **Pivotes:** abajo a la izquierda del módulo, con **sockets** nombrados (`socket_top`, `socket_right`) para encajar las piezas.
- **Nombres:** `estilo_tipo_variante_lod` (por ejemplo, `med_balcon_forja_02_l0`).
- **UV:** a la paleta del estilo y canal UV2 con un id de ventana para la iluminación nocturna.
- **Máscara de tinte en el color de vértice:** R = fachada, G = carpintería, B = toldos o cubierta, A = suciedad.

### 13.6 Pipeline de producción de assets

```
1. Fuente
   ├─ a) Packs CC0 (Kenney, Quaternius, KayKit...) → base rápida y legal
   ├─ b) ComfyUI en el PC (imagen → 3D) → piezas y props con identidad propia
   └─ c) Modelado a mano en Blender (emblemáticos, módulos clave)
2. Limpieza en Blender (headless, script Python)
   ├─ Unificar escala y orientación, pivote y sockets
   ├─ Reducir triángulos al presupuesto (decimate + limpieza)
   ├─ Recalcular normales (facetado)
   ├─ Re-mapear UV a la paleta (cada cara → color de paleta más cercano)
   ├─ Hornear la AO y la máscara de tinte en el color de vértice
   ├─ Generar LOD1/LOD2
   └─ Exportar FBX o GLB + JSON de metadatos (tipo, sockets, tamaños)
3. Unity (AssetPostprocessor automático)
   ├─ Material compartido, LODGroup, etiquetas de Addressables
   ├─ Registro en el catálogo de piezas (ScriptableObject + JSON)
   └─ Validación: triángulos, tamaño, nombres, sockets → informe
4. Animaciones: Mixamo o CC0 → Blender → horneado VAT → importador VAT en Unity
```

- **Generación con IA (ComfyUI):**
  - Se generan imágenes de referencia de cada pieza con un *prompt* de estilo fijo.
  - Se pasan a un modelo imagen → 3D y se limpian con el script.
  - Solo se aceptan las piezas que superan la validación automática y una revisión visual (capturas automáticas que revisa Claude y, en caso de duda, Álvaro).
- **Iconos e interfaz:**
  - Iconos planos de 2 colores generados con IA o dibujados en Figma/Adobe y vectorizados.
  - Fuente con licencia OFL (por ejemplo, Inter o Nunito) con caracteres latinos ampliados.
- **Arte de tienda:** capturas en el modo foto, retocadas en Canva o Adobe. Tráiler con capturas de vídeo del juego.

### 13.7 Política de licencias de assets

| Licencia | ¿Se puede usar? | Condición |
|---|---|---|
| CC0 / dominio público | ✅ Sí | Ninguna (se agradece en los créditos) |
| CC-BY | ✅ Sí | **Atribución obligatoria** en los créditos |
| CC-BY-SA | ⚠️ Evitar | Obliga a compartir los derivados con la misma licencia |
| CC-BY-NC / NC-SA | ❌ No | Prohíbe el uso comercial |
| Unity Asset Store (EULA estándar) | ✅ Sí | Solo dentro del juego, no se redistribuye en bruto |
| Mixamo | ✅ Sí | Permitido en juegos; no se redistribuyen los archivos originales |
| Fuentes OFL | ✅ Sí | Incluir la licencia |
| Generado con IA en local (ComfyUI) | ✅ Sí | Revisar la licencia del **modelo** usado (algunos prohíben el uso comercial) |
| "Ripeado" de otros juegos | ❌ Nunca | — |

- **Registro obligatorio** en `ASSETS_LICENSES.md`: archivo, fuente (URL), autor, licencia, fecha y modificaciones.
- La pantalla **Créditos y licencias** se genera desde ese registro.

---

## 14. Sonido y música

### 14.1 Banda sonora original

- **Origen:** banda sonora **100 % original de URBE**, **compuesta y sintetizada por código** con un generador propio (`tools/music/`, Python + numpy). No depende de licencias de terceros. Se regenera de forma idéntica con la misma semilla y se amplía añadiendo definiciones de pistas nuevas.
- **Estilo:** **electrónica cálida / lo-fi**: pads suaves, piano eléctrico FM, bajo redondo, plucks y campanas, batería suave y un leve crujido de vinilo. Acompaña sin cansar durante sesiones largas.
- **v1 (generada el 2026-10-28):**

| # | Pista | Momento de uso | Tempo y tonalidad |
|---|---|---|---|
| 1 | Plano Inicial | Menú principal | 76 BPM, Re mayor |
| 2 | Primeras Calles | Mañana / inicio de partida | 84 BPM, Sol mayor |
| 3 | Hora Punta | Día con mucha actividad | 92 BPM, La menor |
| 4 | Barrio al Sol | Tarde | 80 BPM, Fa mayor |
| 5 | Luces de Neón | Noche | 70 BPM, Mi dórico |
| 6 | Skyline | Ciudad grande / hitos altos | 88 BPM, Si♭ mayor |
| J1 | Hito alcanzado | Jingle al desbloquear un hito | — |
| J2 | Logro | Jingle de logro | — |

- **Capas adaptativas:** cada pista se exporta también en 3 capas sincronizadas:
  - `base` (pads + bajo)
  - `melodia`
  - `ritmo`

  El juego las combina según el momento:

  | Estado | Capas |
  |---|---|
  | Calma (pausa, zoom lejano, noche tranquila) | Base |
  | Normal | Base + melodía |
  | Activa (hora punta, eventos, construcción intensa) | Las tres |

  Los cambios son fundidos de 4 s, siempre en el compás.
- **Bucles sin cortes:** la cola de la reverberación del final se suma al principio, así que la pista se repite sin chasquidos ni silencios.
- **Lista de reproducción:** por momento del día y tamaño de la ciudad, con silencios de 20–60 s entre pistas para no fatigar.
- **Para la 1.0:** de 8 a 10 pistas, ampliadas con el mismo generador.
- **Más adelante:** el director valorará añadir música con licencia (CC0/CC-BY) o generada con IA en el PC.
- **Cómo se regenera:** `python3 tools/music/render.py --all` (mezclas) y `--stems` (capas). Ver [PLAN_DE_TRABAJO.md](PLAN_DE_TRABAJO.md).

### 14.2 Sonido ambiente y efectos

- **Ambiente por zonas:**
  - Pájaros y viento en las afueras, rumor de tráfico en el centro, zumbido industrial, olas en la costa y grillos de noche.
  - Mezcla por densidad en pantalla, **no por fuente individual**.
- **Tráfico agregado:** se calcula la densidad de tráfico cerca de la cámara y se ajusta el volumen de 3 capas en bucle (fluido, denso, atasco con bocinas). Cero fuentes de audio por coche.
- **Sirenas:** las emergencias visibles cercanas tienen una fuente propia (máximo 4).
- **Interfaz:** sonidos suaves y cortos, con vibración opcional.
- **Formatos:** Vorbis; música en streaming; efectos en mono a 22–32 kHz. **Presupuesto: ≤ 25 MB** en la descarga base, de los que como máximo **15 MB son música** (capas a calidad media). Música extra en el pack HD opcional.

---

## 15. Optimización y rendimiento

### 15.1 Gamas de dispositivo

| | Baja | Media | Alta | PC |
|---|---|---|---|---|
| Ejemplo | 3–4 GB de RAM, GPU de entrada (2020–2021) | 6 GB, gama media de 2022 o posterior | 8+ GB, gama alta reciente | 16 GB de RAM, GPU dedicada |
| FPS objetivo | 30 | 30 (60 opcional) | 60 | 60+ |
| Escala de resolución | 60–75 % dinámica | 75–90 % dinámica | 100 % | 100 % |
| Sombras | No | Solo cerca | Cerca y media | Completas |
| Vehículos y peatones físicos | 1.200 / 1.200 | 3.000 / 3.000 | 6.000 / 6.000 | 16.000 / 16.000 |
| Actualización de capas | 2 s | 1 s | 0,5 s | 0,25 s |
| Densidad de decoración | 40 % | 70 % | 100 % | 100 % |
| Memoria máxima del juego | 1,2 GB | 1,8 GB | 2,5 GB | 6 GB |

- **Detección:** al primer arranque, una prueba corta de unos 5 s (RAM, GPU, rendimiento en una escena de prueba) elige la gama. El jugador puede cambiarla.
- **Ajuste en marcha:** si el fps medio cae por debajo del objetivo durante 10 s, se bajan escalones (primero la resolución, luego las sombras, luego la decoración y por último los agentes visibles).

### 15.2 CPU

- **Simulación fuera del hilo principal:** jobs y Burst en los núcleos de trabajo; el hilo principal solo hace render, entrada e interfaz.
- **Patrón "referencia + optimizado":**
  - **`Sim.Core`** (C# puro) es la **implementación de referencia**, testeable en la nube.
  - Los **núcleos calientes** (movimiento de vehículos, campos de rejilla, lotes de pathfinding, ensamblado de edificios) tienen una versión **Burst** en `Game.Burst`.
  - Un test verifica que ambas producen el mismo resultado con las mismas entradas.
  - Solo se porta a Burst lo que el profiler señala.
- **Sin recolector de basura en partida:** pools, `NativeArray`, `Span<T>`, y nada de LINQ ni de cadenas en los bucles calientes.
- **Reparto en el tiempo:** pathfinding, isócronas, campos, edificios y estadísticas se procesan por porciones con un presupuesto fijo por fotograma.
- **IL2CPP + ARM64** en Android, con eliminación de código no usado alta y ensamblados mínimos.

### 15.3 GPU

- **Instancing masivo:**
  - Una sola paleta y material permiten dibujar miles de piezas de edificio, vehículos, árboles y peatones con muy pocas llamadas.
  - Se usan **BatchRendererGroup / GPU Resident Drawer** de Unity 6. Están documentados para URP con Vulkan, pero hay informes de regresiones en algunos casos, así que **se mide en dispositivo real**.
  - Si no compensa, se usa `Graphics.RenderMeshInstanced` / `RenderMeshIndirect` con nuestro propio culling.
- **Culling por trozos** de 256 m (frustum) y oclusión de GPU **solo si mejora en las mediciones** (se activa y desactiva por separado).
- **LOD e impostores** (ver [13.3](#133-niveles-de-detalle-e-impostores)) y **niebla** para limitar la distancia de dibujado.
- **Vulkan** por defecto en Android (con lista de exclusión de Unity para los dispositivos problemáticos) y OpenGL ES 3 como alternativa.
- **Resolución dinámica** y límite de fps.
- **Variantes de shader:** se eliminan todas las que no se usan (recorte en la build). Son pocos shaders: edificios, vehículos, VAT, terreno, agua, capas, interfaz.

### 15.4 Memoria

| Partida | Gama media (100.000 habitantes), presupuesto |
|---|---|
| Simulación (ciudadanos, edificios, red, campos, viajes) | 120 MB |
| Mallas (kit de piezas, vehículos, servicios, LOD) | 150 MB |
| Texturas (paletas, atlas, VAT, interfaz) | 120 MB |
| Búferes de instancias y render | 80 MB |
| Audio (streaming) | 30 MB |
| Runtime de Unity y otros | 300 MB |
| **Total** | **≈ 800 MB** (margen hasta 1,8 GB) |

- **Addressables** por estilo: solo se cargan los estilos presentes en la ciudad.
- **Texture streaming** (mipmaps) en PC y gama alta.

### 15.5 Batería y temperatura

- **Límite de 30 fps** por defecto en el móvil; 60 opcional.
- **Pausa:** al pausar, el render baja a 15 fps (solo animaciones de interfaz). En segundo plano, todo se para y se autoguarda.
- **Adaptive Performance** (proveedores disponibles en Android): reacciona a los avisos térmicos bajando escalones antes de que el sistema estrangule.
- **Modo ahorro** en los ajustes: 30 fps fijos, resolución al 70 % y menos agentes visibles.

### 15.6 Medición continua

- **Marcadores del profiler** por sistema (`Sim.Traffic`, `Sim.Pathfinding`, `Render.Instances`…).
- **Panel de depuración** dentro del juego: fps, ms por sistema, agentes, cola de rutas, memoria.
- **Escenas de referencia** (ciudades de 25.000, 50.000, 100.000 y 200.000 habitantes) con una cámara grabada. Se ejecutan en el dispositivo mediante `adb` y guardan un CSV de rendimiento (ver [PLAN_DE_TRABAJO.md](PLAN_DE_TRABAJO.md)).
- **Benchmark de `Sim.Core`** en la nube en cada commit (`Sim.Runner --bench`).

---

## 16. Tamaño y almacenamiento

### 16.1 Tamaño de la instalación

| Componente | Presupuesto |
|---|---|
| Código (IL2CPP ARM64) | 25 MB |
| Runtime de Unity | 20 MB |
| Mallas (kit modular de 5 estilos, servicios, vehículos, peatones, decoración) | 30 MB |
| Texturas (paletas, atlas, VAT, interfaz, terreno) | 25 MB |
| Audio (música base + ambiente + efectos) | 25 MB |
| Datos (JSON comprimido), fuentes, localización | 5 MB |
| Mapas (alturas comprimidas, 6 mapas) | 10 MB |
| **Total de descarga base** | **≈ 140 MB** (objetivo ≤ 150 MB) |

- **Pack HD opcional** mediante Play Asset Delivery (*on-demand*): música extra, texturas de mayor resolución para tablets y gama alta, y mapas adicionales.
- El módulo base de un AAB tiene un límite de tamaño comprimido (200 MB según la documentación de Google Play; se verifica al publicar).

### 16.2 Tamaño de las partidas guardadas

**Se guarda solo el estado autoritativo.** Todo lo visual se regenera desde las semillas.

| Bloque (ciudad de 100.000 habitantes) | Cálculo | Bruto |
|---|---|---|
| Ciudadanos | 100.000 × 12 B (empaquetado de bits) | 1,2 MB |
| Hogares | 40.000 × 8 B | 0,3 MB |
| Edificios | 15.000 × 16 B (arquetipo, semilla, nivel, estado, problemas) | 0,24 MB |
| Red viaria | 20.000 segmentos × 40 B + nodos | 1,0 MB |
| Carriles, intersecciones y semáforos (solo lo editado) | Variable | 0,1 MB |
| Zonas y distritos | Celdas de zona codificadas por tramos (RLE) | 0,2 MB |
| Campos de rejilla | 6 campos × 35.000–140.000 celdas × 1 B | 0,5 MB |
| Terreno | **Solo las diferencias** con el mapa base | 0,1–2 MB |
| Viajes y vehículos activos | 8.000 × 24 B | 0,2 MB |
| Transporte, economía, políticas, planos | — | 0,1 MB |
| Historial de estadísticas (con resolución reducida) | — | 0,3 MB |
| **Total bruto** | | **≈ 4–6 MB** |
| **Comprimido (Zstd)** | | **≈ 1,5–3 MB** ✅ |

### 16.3 Técnicas de compresión

- **Semillas en lugar de geometría:** edificios, nombres de ciudadanos y calles, colores de vehículos y la posición de los props generados se derivan de `(semillaPartida, id)`.
- **Empaquetado de bits:** edad (7 bits), educación (2), salud (7), felicidad (7), banderas, etc.
- **Varint y deltas:** los ids y las coordenadas se guardan como diferencias respecto al anterior y en enteros de longitud variable.
- **Diferencias de terreno:** solo las celdas modificadas respecto al mapa original, que viene con el juego.
- **Compresión final:** **Zstd** (nivel rápido en el autoguardado, máximo en el guardado manual) o **LZ4** en la gama baja si el tiempo de guardado importa.
- **Historial con resolución decreciente:** últimas 48 h por hora → últimos 2 meses por día → el resto por mes.

### 16.4 Formato y versionado

```
Cabecera: "URBE" | versión del formato | versión del juego | semilla | fecha | miniatura (JPEG 256×144)
Bloques: [tipo (4 bytes) | versión del bloque | longitud | datos comprimidos | CRC32] × N
```

- Bloques independientes con **migraciones** por versión: las partidas viejas siempre cargan.
- Los bloques desconocidos se conservan al guardar (compatibilidad con mods futuros).

### 16.5 Guardado robusto

- **Escritura atómica:** archivo temporal → fsync → renombrar. Si falla, la partida anterior queda intacta.
- **Autoguardado rotativo** con 3 ranuras (configurable de 1 a 5). Nunca crece sin control.
- **Avisos:** si las partidas del jugador superan 200 MB, se le avisa y se le ofrece borrar los autoguardados antiguos.
- **Tiempo de guardado** en la gama media con 100.000 habitantes: < 1 s (serialización en un hilo de fondo sobre una copia del estado).

### 16.6 Caché regenerable

- **Contenido:** impostores de manzana, *landmarks* de pathfinding, cachés de rutas y mallas de carreteras precalculadas.
- Va en la carpeta de caché del sistema, se puede **borrar sin perder nada** y el sistema la puede purgar.
- **Límite:** 200 MB, con política LRU.

### 16.7 Memoria con ciudades grandes

La simulación crece de forma lineal y compacta: unos 1,2 KB por habitante incluyendo todo, es decir, **unos 120 MB con 100.000 habitantes**. El render no crece con la población, sino con lo **visible**, gracias al instancing y al nivel de detalle.

---

## 17. Sin conexión y servicios opcionales

| Función | ¿Necesita conexión? |
|---|---|
| Jugar (todos los modos), guardar y cargar | ❌ No |
| Desbloqueo del juego completo | Solo **en el momento de la compra**. Después, la licencia se guarda en caché y se revalida de forma oportunista cuando hay conexión, con un margen amplio |
| Guardado en la nube | ✅ Opcional (Google Play Games, Steam Cloud) |
| Logros sincronizados | ✅ Opcional |
| Informes de errores y estadísticas anónimas | ✅ Opcional, apagado por defecto. Se encolan sin conexión |
| Mods o taller (futuro) | ✅ Opcional |

- **Sin cuentas propias** ni servidores propios necesarios. Cero costes de mantenimiento de servidores para el proyecto.
- **Privacidad:** no se recogen datos personales. El formulario de seguridad de datos de Google Play será sencillo.

---

## 18. Monetización y distribución

### 18.1 Modelo

- **Descarga gratuita (demo completa en mecánicas):**
  - Modo carrera hasta el hito 4 (Ciudad pequeña, 5.000 habitantes).
  - 2 mapas, 2 estilos y sandbox limitado a una casilla.
- **Desbloqueo único** (precio orientativo **6,99–9,99 €**, a validar con el mercado): todo sin límites, todos los mapas y estilos, sandbox y diorama completos, escenarios y el editor cuando salga.
- **Después del lanzamiento (opcional):**
  - **Packs de estilos** arquitectónicos y mapas (cosméticos o de contenido, nunca ventajas).
  - **Banda sonora.**
- **Nunca:** anuncios, temporizadores, energía, gemas, lootboxes ni compras que aceleren.

### 18.2 Distribución

| Tienda | Formato | Notas |
|---|---|---|
| **Google Play** | AAB + Play Asset Delivery | **Nivel de API objetivo:** desde el 31-08-2026, las apps nuevas y las actualizaciones deben apuntar a **Android 16 (API 36)**. Las cuentas personales nuevas deben pasar una **prueba cerrada** (más de 12 probadores durante 14 días; se verifica la cifra vigente) antes de producción. Cuenta de desarrollador: pago único de 25 $ |
| **Steam** | Windows x64 (+ Linux / Steam Deck si es viable) | Steam Direct (tarifa por aplicación). DRM opcional: se publicará sin DRM obligatorio |
| **itch.io** | Windows / Linux | Ideal para la demo temprana y la comunidad |
| iOS | — | **Descartado** (decisión del director) |

### 18.3 Comunidad y marketing (coste cero o bajo)

- **Devlog** quincenal: GIFs del tráfico, de los edificios procedurales y de "la cadena de causas".
- **Clips cortos** (TikTok, YouTube Shorts, Reddit: r/CitiesSkylines, r/citybuilders, r/AndroidGaming).
- **Demo jugable temprana** en itch.io y prueba cerrada en Google Play con la comunidad.
- **Arte de tienda y tráiler:** modo foto + Canva o Adobe.

---

## 19. Arquitectura técnica

### 19.1 Tecnologías

| Área | Elección |
|---|---|
| Motor | Unity 6 LTS (rama 6000.x vigente) |
| Render | URP con perfil móvil (Forward+), Vulkan en Android |
| Entrada | Input System (táctil, ratón, teclado, mando en PC opcional) |
| Interfaz | UI Toolkit |
| Rendimiento | Burst, Collections, Mathematics, Jobs; Adaptive Performance |
| Contenido | Addressables + JSON de datos |
| Localización | Paquete Localization |
| Compras | Unity IAP (Google Play Billing) en Android; Steamworks.NET en Steam |
| Simulación | `Sim.Core` (C# netstandard2.1, sin Unity) |
| Tests | xUnit o NUnit (`dotnet test`) para `Sim.Core`; Unity Test Framework para lo mínimo de Unity |
| Licencia de Unity | Unity Personal mientras los ingresos estén por debajo del umbral vigente (se verifica al publicar) |

### 19.2 Ensamblados

```
Sim.Core        → simulación pura (sin Unity). Referencia de todo.
Sim.Tests       → tests unitarios, de repetición y de rendimiento (dotnet).
Sim.Runner      → consola: ciudades bot, benchmark, visor PNG (dotnet).
Game.Unity      → render, cámara, entrada, interfaz, audio, puente con Sim.Core.
Game.Burst      → versiones Burst de los núcleos calientes (validadas contra Sim.Core).
Game.Editor     → importadores (VAT, kit modular), validadores de assets, herramientas.
```

### 19.3 Estructura del repositorio (prevista)

```
/docs                    GDD, plan de trabajo, decisiones (ADR)
/sim
  /Sim.Core              código de la simulación
  /Sim.Tests
  /Sim.Runner
  Sim.sln
/unity/URBE              proyecto de Unity
  /Assets/_Project
    /Scripts             Game.Unity, Game.Burst, Game.Editor
    /Art                 kit modular por estilo, vehículos, peatones (VAT), terreno
    /UI                  UXML, USS, iconos
    /Audio
    /Data                JSON (edificios, estilos, carreteras, servicios, políticas, equilibrio)
    /Scenes
  /Packages
/tools
  /blender               scripts de Python (limpieza, LOD, paleta, VAT)
  /comfyui               workflows JSON y prompts
  /scripts               utilidades (validaciones, informes)
/ASSETS_LICENSES.md
```

`Sim.Core` se incluye en Unity como **paquete local** o como **DLL compilada** desde `/sim`, para que haya una sola fuente de verdad.

### 19.4 Datos

Todo lo balanceable vive en JSON validado por esquemas (JSON Schema): edificios, estilos, carreteras, vehículos, servicios, políticas, hitos y constantes. **Ventajas:**
- Lo pueden editar los modelos baratos (Haiku).
- Se balancea sin recompilar.
- Prepara los mods.

### 19.5 Puente simulación ↔ Unity

- `SimHost` (MonoBehaviour) posee el `SimState` y ejecuta los ticks según la velocidad.
- **Entrada:** la interfaz crea **comandos** y los encola en `SimHost` (nunca toca el estado directamente).
- **Salida:** `SimHost` publica **snapshots** y **eventos** (edificio creado, nivel subido, problema, notificación). Los sistemas de render y de interfaz se suscriben.
- **Interpolación:** el render interpola las posiciones de los agentes entre los dos últimos snapshots.

### 19.6 Estrategia de pruebas

| Tipo | Dónde | Qué |
|---|---|---|
| Unitarias | Nube (`dotnet test`) | Geometría Bézier, grafo, A\*, IDM, despacho, redes, economía, empaquetado del guardado |
| Repetición (*golden*) | Nube | Ciudad de prueba + lista de comandos + N ticks → el hash de estado debe coincidir con el guardado. Detecta cambios de comportamiento no deseados |
| Propiedades | Nube | Invariantes: nunca hay dos vehículos en el mismo punto del carril; el dinero cuadra; los ciudadanos no se pierden |
| Rendimiento | Nube + dispositivo | `Sim.Runner --bench` en cada commit; escenas de referencia en el móvil |
| Unity (PlayMode) | PC | Humo: cargar escena, colocar carretera, guardar y cargar |
| Visual | PC + dispositivo | Capturas automáticas de escenas fijas para comparar cambios |

### 19.7 Integración continua

- **GitHub Actions:** `dotnet build` + `dotnet test` + benchmark corto en cada push. Es barato y rápido.
- **Builds de Unity:** en el PC de desarrollo con Unity CLI. Más adelante, opcional con GameCI en Actions (requiere secretos de la licencia de Unity).

---

## 20. Riesgos y decisiones

### 20.1 Riesgos de diseño y técnicos

| Riesgo | Prob. | Impacto | Mitigación |
|---|---|---|---|
| Rendimiento insuficiente en la gama baja | Media | Alto | Gamas, tráfico híbrido, mediciones desde M2 en un dispositivo real, escenas de referencia |
| El tráfico se comporta raro (bloqueos, carriles absurdos) | Alta | Alto | Elección de carril anticipada, anti-bloqueo, tests de propiedades, herramientas de depuración visuales |
| La variedad procedural se ve "rara" o incoherente | Media | Medio | Reglas de coherencia por estilo, revisión visual con capturas, empezar con 2 estilos muy pulidos |
| Assets generados con IA de calidad irregular | Alta | Medio | Validación automática + revisión; base CC0 sólida; los emblemáticos se hacen a mano |
| Alcance excesivo | Alta | Alto | Hitos con versión jugable en cada uno; lo que no entra en la 1.0 va a una lista "después del lanzamiento" |
| Interfaz táctil confusa en herramientas complejas (editor de cruces) | Media | Medio | Prototipos en Figma, pruebas con usuarios reales en la prueba cerrada, modo experto oculto |
| Cambios de requisitos de Google Play | Media | Medio | Revisar los requisitos en cada hito (API objetivo, formularios) |
| Riesgo legal por copiar diseño o assets de terceros | Baja | Muy alto | Sala limpia, registro de licencias, sin herramientas de ingeniería inversa |

### 20.2 Decisiones tomadas (2026-10-28)

| Tema | Decisión del director |
|---|---|
| Nombre | **URBE sigue siendo provisional.** Se elige el definitivo antes de la ficha de la tienda (M7) |
| Contenido de la 1.0 | **6 mapas y 5 estilos:** Mediterráneo, Ensanche europeo, Nórdico, Suburbio norteamericano y Latinoamericano. Asia densa, Modernista y Eco-futurista llegan después |
| iOS | **Descartado** |
| Mods | **Mods de datos en móvil y PC**, después del lanzamiento (puede cambiar) |
| Modo vertical | **Sí, en la 1.0**, como modo secundario; el diseño principal es horizontal (ver [11.2](#112-distribución-en-el-móvil-horizontal)) |
| Música | **Banda sonora original generada por código** (ver [14.1](#141-banda-sonora-original)). Más adelante se valorará añadir música con licencia |
| Ingeniería inversa | **No se usan herramientas de ingeniería inversa** (REA descartado) |

---

## 21. Glosario

| Término | Significado |
|---|---|
| **Agente** | Entidad que se mueve físicamente en la simulación: vehículo, peatón o pasajero |
| **ALT** | Heurística para A\* basada en *landmarks* y la desigualdad triangular; acelera mucho las rutas |
| **AAB / PAD** | Android App Bundle / Play Asset Delivery: formato de publicación y entrega de paquetes de assets en Google Play |
| **Bézier** | Curva definida por puntos de control; se usa para las carreteras |
| **Burst / Jobs** | Compilador y sistema de tareas de Unity para código paralelo y muy rápido |
| **Cadena de causas** | Explicación de un problema desde el síntoma hasta el origen |
| **Draw call** | Orden de dibujo a la GPU; menos es mejor |
| **Gama** | Nivel de calidad y rendimiento según el dispositivo |
| **IDM** | *Intelligent Driver Model*: modelo matemático de aceleración y frenada de los coches |
| **Impostor** | Imagen precalculada que sustituye a un modelo 3D lejano |
| **Instancing** | Dibujar muchas copias de una malla en una sola llamada |
| **Isócrona** | Zona alcanzable en un tiempo dado por la red real |
| **LOD** | *Level of Detail*: versiones más simples de un modelo según la distancia |
| **MOBIL** | Modelo de decisión de cambio de carril |
| **Modo plano** | Construcción en borrador, sin coste hasta que se confirma |
| **Semilla** | Número que determina de forma reproducible un resultado aleatorio |
| **Snapshot** | Copia compacta del estado que la simulación entrega al render |
| **Tick** | Paso fijo de la simulación (10 por segundo a ×1) |
| **Tráfico híbrido** | Mezcla de agentes físicos y flujo estadístico para escalar sin perder las consecuencias |
| **VAT** | *Vertex Animation Texture*: animación horneada en una textura y reproducida en la GPU |
