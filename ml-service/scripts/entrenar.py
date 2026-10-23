"""
Entrena el modelo de inasistencia con el historial de SQL Server y lo deja activo.

Uso (desde la carpeta ml-service):  python -m scripts.entrenar
"""

import logging

from app.servicio import servicio

if __name__ == "__main__":
    logging.basicConfig(level=logging.INFO, format="%(levelname)s %(message)s")
    meta = servicio.entrenar()
    print(f"\nModelo {meta['version']}  ·  {meta['muestras']} citas  ·  AUC {meta['auc']:.3f}")
    print(f"Tasa de inasistencia: {meta['tasaBase']:.1%}  ·  precisión nivel ALTO: {meta['precisionAlto']:.1%}"
          f"  ·  cobertura nivel ALTO: {meta['recallAlto']:.1%}\n")
    print("Variables con más peso:")
    for c in meta["coeficientes"][:8]:
        print(f"  {c['variable']:<40} coef {c['coeficiente']:+.3f}   odds ×{c['oddsRatio']}")
    print(f"\nCitas próximas recalculadas: {servicio.recalcular_futuras()}")

