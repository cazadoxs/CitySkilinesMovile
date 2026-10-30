# Bocetos de UI (M0-06)

Archivo de Figma: https://www.figma.com/design/bVqJsyD1BPxfXwHnldr1Ih (cuenta de Álvaro, borrador del equipo)

## Páginas
- **Bocetos**: 8 pantallas (6 horizontales 844×390 lógico, 2 verticales 390×844).
- **Componentes**: botón, botón de icono, pestaña, tarjeta/panel, barra RCI, chip de notificación.

## Pantallas
1. Menú principal (Continuar grande, Nueva/Cargar/Escenarios/Ajustes, almacenamiento) — GDD §12.2
2. HUD en partida horizontal — §11.2
3. Barra de construcción abierta (categoría Carreteras, pestañas y tarjetas) — §12.7
4. Panel de edificio con cadena de causas (Calle Olivo 14, "Ir al cruce" / "Ver ruta del camión") — §8.8, §11.5
5. Herramienta de carreteras (fantasma, Recta/Curva/Libre, ajuste 15°, lupa, ✓, coste en vivo, motivo en rojo, ▲▼ y ⇄) — §5.3
6. Panel de capas de información (con nota de paleta daltónica y leyenda con patrón) — §11.9
7. HUD vertical (barra compacta, velocidad/avisos, categorías en 2 filas) — §11.2
8. Panel de edificio vertical (hoja deslizable desde abajo) — §11.2

## Decisiones de diseño
- Wireframe en grises y azul de acento; rojo solo para problema/error, verde para confirmar (✓).
- Objetivos táctiles de 48 dp en botones y botones de icono; el mapa ocupa la mayor parte de la pantalla (§11.1).
- Acciones frecuentes abajo (categorías, ✓/cancelar, coste); paneles de información a la derecha en horizontal y como hoja inferior en vertical.
- La cadena de causas va en un bloque rojo claro con la frase del GDD y dos botones de "ir a".
- Los iconos son marcadores de posición (texto corto); el arte final queda fuera de esta tarjeta.
- Las pantallas usan instancias de los componentes; los cambios en "Componentes" se propagan.
