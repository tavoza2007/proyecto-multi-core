using PrecioPixel.Config;
using PrecioPixel.Modelos;

namespace PrecioPixel.Motor;

/// <summary>
/// ¿Cómo sabemos si un juego está en oferta? Se combinan DOS criterios:
///
///  1) Criterio "tienda": la tienda publica un precio regular mayor al actual (precio tachado).
///     Descuento = (regular − actual) / regular.
///
///  2) Criterio "referencia": la tienda no marca descuento, pero el precio actual está al menos
///     UmbralContraReferenciaPct % por debajo del precio de referencia (MSRP) que guardamos en el repo.
///     Descuento = (referencia − actual) / referencia.
///
/// Además, a nivel juego se marca "Igual al mínimo histórico" comparando con corridas anteriores.
/// </summary>
public sealed class DetectorOfertas(OfertasOpciones opt)
{
    public void Evaluar(PrecioTienda p, Juego juego)
    {
        if (p.PrecioRegular > p.PrecioActual + 0.009)
        {
            p.EnOferta = true;
            p.MotivoOferta = "tienda";
            p.DescuentoPct = Porcentaje(p.PrecioRegular, p.PrecioActual);
        }
        else if (juego.PrecioReferencia > 0 &&
                 p.PrecioActual <= juego.PrecioReferencia * (1 - opt.UmbralContraReferenciaPct / 100.0))
        {
            p.EnOferta = true;
            p.MotivoOferta = "referencia";
            p.DescuentoPct = Porcentaje(juego.PrecioReferencia, p.PrecioActual);
        }
        else
        {
            p.EnOferta = false;
            p.MotivoOferta = null;
            p.DescuentoPct = 0;
        }
    }

    private static int Porcentaje(double regular, double actual) =>
        regular <= 0 ? 0 : (int)Math.Round((regular - actual) / regular * 100);

    /// <summary>Precio que se muestra tachado en la tarjeta para una fila.</summary>
    public static double PrecioTachado(PrecioTienda p, Juego juego) =>
        p.MotivoOferta == "referencia" ? juego.PrecioReferencia : p.PrecioRegular;
}
