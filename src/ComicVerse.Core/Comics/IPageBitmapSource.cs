namespace ComicVerse.Core.Comics;

/// <summary>可选能力（桌面端）：直接产出位图，避免“渲染 → PNG 编码 → 再解码”的往返开销。</summary>
public interface IPageBitmapSource
{
    System.Windows.Media.Imaging.BitmapSource? RenderPageBitmap(int index);
}
