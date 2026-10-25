# Revisión del modelo de inasistencia (F5b)

Generado con `python -m scripts.revision_modelo` sobre 21.234 citas **sintéticas** (18 meses, semilla fija). Validación temporal: entrena con el 80 % más antiguo y evalúa con el 20 % más reciente. La ficha del modelo (`ficha-modelo.md`) explica cómo leer estas tablas y las decisiones tomadas.

## Desempeño global

| Métrica | Valor | Mínimo para activarse |
|---|---|---|
| AUC | 0.736 | 0.60 |
| Brier (error de las probabilidades) | 0.1375 | menor que la tasa base (0.1559) |
| Precisión del nivel ALTO | 41.8% | mayor que la tasa base (19.3%) |
| Cobertura del nivel ALTO | 43.2% | — |

## Calibración por decil

| Decil | Probabilidad media | Inasistencia real | Citas |
|---|---|---|---|
| 1 | 4.3% | 4.5% | 425 |
| 2 | 7.0% | 7.5% | 425 |
| 3 | 9.2% | 8.0% | 424 |
| 4 | 11.5% | 11.8% | 425 |
| 5 | 14.0% | 11.3% | 425 |
| 6 | 16.9% | 17.4% | 424 |
| 7 | 20.3% | 22.4% | 425 |
| 8 | 24.6% | 26.9% | 424 |
| 9 | 31.0% | 32.2% | 425 |
| 10 | 45.7% | 51.3% | 425 |

## Equidad por grupo

*Falsos positivos*: citas a las que el paciente sí asistió pero quedaron en ALTO (reciben un recordatorio o una confirmación de más). *Cobertura*: inasistencias reales que quedaron en ALTO.

### Sexo

| Sexo | Citas | Inasistencia real | Prob. media | % ALTO | Falsos positivos | Cobertura | AUC |
|---|---|---|---|---|---|---|---|
| Hombre | 1971 | 17.7% | 17.8% | 17.9% | 12.3% | 44.0% | 0.747 |
| Mujer | 2276 | 20.8% | 19.0% | 21.9% | 16.4% | 42.7% | 0.725 |

### Previsión (no es variable del modelo)

| Previsión (no es variable del modelo) | Citas | Inasistencia real | Prob. media | % ALTO | Falsos positivos | Cobertura | AUC |
|---|---|---|---|---|---|---|---|
| FONASA | 2890 | 20.1% | 18.8% | 20.8% | 14.8% | 44.5% | 0.737 |
| ISAPRE | 1133 | 17.3% | 17.7% | 18.0% | 13.7% | 38.8% | 0.724 |
| PARTICULAR | 224 | 20.1% | 17.9% | 20.5% | 14.0% | 46.7% | 0.764 |

### Tramo de edad

| Tramo de edad | Citas | Inasistencia real | Prob. media | % ALTO | Falsos positivos | Cobertura | AUC |
|---|---|---|---|---|---|---|---|
| 0 a 14 | 509 | 13.2% | 10.8% | 4.5% | 2.9% | 14.9% | 0.724 |
| 15 a 39 | 1210 | 24.5% | 24.2% | 35.4% | 27.0% | 61.1% | 0.727 |
| 40 a 64 | 1905 | 20.4% | 18.4% | 18.5% | 13.2% | 39.3% | 0.732 |
| 65 o más | 623 | 11.1% | 13.7% | 7.4% | 6.3% | 15.9% | 0.682 |

### Sede

| Sede | Citas | Inasistencia real | Prob. media | % ALTO | Falsos positivos | Cobertura | AUC |
|---|---|---|---|---|---|---|---|
| Las Condes | 1619 | 20.6% | 20.5% | 25.0% | 17.9% | 52.4% | 0.752 |
| Providencia | 2628 | 18.5% | 17.2% | 16.9% | 12.4% | 37.0% | 0.727 |

## Resumen de disparidad

| Grupo | Razón de % ALTO (máx/mín) | Diferencia de falsos positivos |
|---|---|---|
| Sexo | 1.22 | 4.1% |
| Previsión (no es variable del modelo) | 1.16 | 1.1% |
| Tramo de edad | 7.87 | 24.1% |
| Sede | 1.48 | 5.5% |
