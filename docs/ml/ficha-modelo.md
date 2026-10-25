# Ficha del modelo de inasistencia

Revisión de la fase 5b. Las cifras salen de [`revision-modelo.md`](revision-modelo.md), que se
regenera con `python -m scripts.revision_modelo` (ml-service) sobre el historial **sintético**.

## Para qué sirve y para qué no

| | |
|---|---|
| Predice | La probabilidad de que el paciente no asista a una cita ya reservada |
| Se usa para | Mostrar el riesgo (Bajo / Medio / Alto) en la agenda del día y decidir a quién se envía además un SMS (riesgo medio y alto) o se le pide confirmar (alto). **Todos** los pacientes reciben el recordatorio por correo |
| **No** se usa para | Negar, cobrar más, priorizar o quitar horas. La reserva nunca espera al modelo y funciona igual si el ml-service está caído |
| Quién lo ve | Personal clínico y de recepción (con auditoría). El paciente no ve su riesgo |
| Algoritmo | Regresión logística (scikit-learn): probabilidades y coeficientes explicables |

## Datos y variables

- **Variables:** días de anticipación, franja horaria, día de la semana, canal, pago anticipado,
  especialidad, sede, edad, citas previas y tasa de inasistencia previa (suavizada para pacientes nuevos).
- **Excluidas a propósito:** previsión, tramo Fonasa, sexo, comuna de residencia y nombre. La previsión y el
  tramo son proxies de nivel socioeconómico; el sexo no aporta a la decisión que se toma.
- **Datos mínimos:** el ml-service lee con un usuario SQL de solo lectura, ve el RUT enmascarado y solo
  desenmascara la fecha de nacimiento (para la edad). No recibe nombres, correos ni teléfonos.
- **Datos de esta demo:** sintéticos (`@sintetico.cl`). Las cifras muestran que el proceso funciona, no cómo
  rendiría con pacientes reales.

## Calidad mínima para activarse (gate)

Cada entrenamiento se evalúa con validación temporal (entrena con el 80 % más antiguo, prueba con el 20 %
más reciente). El modelo nuevo **solo reemplaza al activo** si cumple todo; si no, queda registrado como
inactivo en `dbo.ModelosML` con el motivo y la API responde 422:

| Criterio | Umbral | Resultado (sintético) |
|---|---|---|
| AUC | ≥ 0,60 | 0,736 |
| Brier (error de las probabilidades) | menor que predecir siempre la tasa base | 0,1375 < 0,1559 |
| Precisión del nivel ALTO | mayor que la tasa base | 41,8 % > 19,3 % |

La calibración por decil también queda guardada: en cada decil la probabilidad media está a menos de
6 puntos de la inasistencia observada (prueba automática con tolerancia de 10).

## Equidad: qué se midió y qué se decidió

El costo de un error es bajo pero no nulo: un **falso positivo** (asistió, pero quedó en ALTO) recibe un
SMS o una solicitud de confirmación de más; un **falso negativo** recibe solo el recordatorio por correo, sin SMS.

| Grupo | Hallazgo | Decisión |
|---|---|---|
| Previsión (no es variable) | % ALTO entre 18 % y 21 %, falsos positivos con 1,1 puntos de diferencia, calibración dentro de cada grupo | Sin efecto de proxy. Queda una **prueba automática** (falla si la calibración por grupo se aleja más de 4 puntos o los falsos positivos difieren más de 6) |
| Sexo (no es variable) | Diferencia de falsos positivos de 4 puntos, que sigue la diferencia real de inasistencia | Aceptado; cubierto por la misma prueba |
| Edad (sí es variable) | 15–39 años concentra el ALTO (35 %) y niños y mayores casi no (5 % y 7 %) | Es un efecto real y documentado; la consecuencia es un SMS más o menos, y el correo de recordatorio llega a todos. **Se mantiene**; con datos reales, evaluar enviar SMS a todos los mayores de 65 sin importar el riesgo |
| Sede (sí es variable) | Las Condes tiene más ALTO (25 % vs 17 %) | Se explica por el horario de tarde de esa sede (franja con más inasistencia), no por la comuna. **Revisar con datos reales**: si persiste sin explicación horaria, quitar la sede |

## Operación

- **Reentrenamiento:** manual desde el backoffice (*Machine learning → Entrenar*), recomendado mensual o
  cuando la tasa observada se aleje más de 5 puntos de la probabilidad media de la última semana.
- **Monitoreo:** cada predicción queda en `dbo.PrediccionesCita`; el desenlace real queda en la cita. La
  página del modelo compara ambos.
- **Seguridad:** los archivos del modelo están firmados (HMAC) y no se cargan si la firma no coincide (M5);
  el servicio solo se alcanza por HTTPS interno desde la API, con API key.
- **Explicabilidad:** cada predicción guarda las 3 variables que más subieron el riesgo; el backoffice muestra
  coeficientes y odds ratios.

## Limitaciones

- Entrenado y evaluado con datos simulados que siguen patrones de la literatura.
- La regresión logística no captura interacciones complejas; con datos reales habría que comparar con un
  modelo de árboles manteniendo la calibración y el gate.
- La demanda semanal (regresión lineal) se revisa aparte: es informativa para planificar agenda, no toma decisiones.
