using System.Collections.ObjectModel;

namespace AmpzMediaBoard.Board;

/// <summary>
/// La lógica PURA de reordenar pestañas, separada del mouse a propósito: el arrastre de la tira
/// (MainWindow, región "Reordenar pestañas") y Ctrl+Shift+RePág/AvPág terminan acá, y así la
/// regla se prueba sin levantar una ventana (tools/BoardProbe, caso 11).
///
/// ⚠ Reordenar mueve SOLO la colección de pestañas (y con ella la tira, que está bindeada). Las
/// BoardView NO se tocan: viven en su propio contenedor (MainWindow.BoardHost) y se cambian por
/// visibilidad, así que su orden ahí es irrelevante. Moverlas las sacaría del árbol visual →
/// sus VideoView perderían la ventana nativa → cada clip tendría que re-montarse (bug #4).
/// </summary>
public static class TabOrder
{
    /// <summary>
    /// Mueve la pestaña de <paramref name="from"/> a <paramref name="to"/> (índices de la
    /// colección, <paramref name="to"/> ya contado SIN la pestaña movida, como
    /// <see cref="ObservableCollection{T}.Move"/>). Devuelve false si no hubo nada que mover.
    ///
    /// Usa Move y no Remove + Insert: Move levanta UN solo aviso de colección, y el panel de la
    /// tira reacomoda los contenedores que ya existen en vez de destruir uno y fabricar otro.
    /// </summary>
    public static bool Move<T>(ObservableCollection<T> items, int from, int to)
    {
        if (from < 0 || from >= items.Count) return false;
        to = Math.Clamp(to, 0, items.Count - 1);
        if (from == to) return false;
        items.Move(from, to);
        return true;
    }

    /// <summary>
    /// A qué índice va la pestaña arrastrada, dado dónde está dibujado su CENTRO ahora.
    ///
    /// Regla: pasa por encima de una vecina cuando su centro cruza la MITAD de esa vecina. Se
    /// mira el centro de la pestaña arrastrada y no el cursor: la pestaña sigue al mouse
    /// conservando el punto de agarre, y lo que el ojo compara es pestaña contra pestaña.
    ///
    /// ⚠ Con la mitad de la VECINA (y no una frontera fija) no hay rebote entre pestañas de
    /// distinto ancho: después del intercambio, la mitad de la vecina queda del otro lado del
    /// centro arrastrado, así que el movimiento siguiente no la devuelve. Con una frontera fija
    /// una pestaña ancha junto a una angosta oscilaría a cada píxel.
    /// </summary>
    /// <param name="slots">Posición (izquierda, ancho) de cada pestaña en su lugar de la tira, en orden.</param>
    /// <param name="dragged">Índice actual de la pestaña arrastrada.</param>
    /// <param name="draggedCenter">Dónde está dibujado el centro de la pestaña arrastrada.</param>
    public static int DropIndex(IReadOnlyList<(double Left, double Width)> slots, int dragged, double draggedCenter)
    {
        var i = dragged;
        while (i + 1 < slots.Count && draggedCenter > slots[i + 1].Left + slots[i + 1].Width / 2) i++;
        if (i != dragged) return i;
        while (i - 1 >= 0 && draggedCenter < slots[i - 1].Left + slots[i - 1].Width / 2) i--;
        return i;
    }
}
