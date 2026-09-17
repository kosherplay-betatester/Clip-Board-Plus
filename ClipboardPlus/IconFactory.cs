using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace ClipboardPlus;
internal static class IconFactory
{
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    private static GraphicsPath Round(float x, float y, float w, float h, float radius)
    {
        var path = new GraphicsPath(); float d = radius * 2;
        path.AddArc(x,y,d,d,180,90); path.AddArc(x+w-d,y,d,d,270,90); path.AddArc(x+w-d,y+h-d,d,d,0,90); path.AddArc(x,y+h-d,d,d,90,90); path.CloseFigure(); return path;
    }
    private static Bitmap Render(int size)
    {
        var bitmap = new Bitmap(size, size);
        using var g = Graphics.FromImage(bitmap); g.SmoothingMode = SmoothingMode.AntiAlias; g.ScaleTransform(size / 64f, size / 64f);
        using var tile = Round(1,1,62,62,14); using var gradient = new LinearGradientBrush(new Point(0,0),new Point(64,64),Color.FromArgb(27,66,76),Color.FromArgb(11,25,39)); g.FillPath(gradient,tile);
        using var paper = new SolidBrush(Color.FromArgb(233,250,243)); using var sheet = Round(15,13,32,41,5); g.FillPath(paper,sheet);
        using var mint = new SolidBrush(Color.FromArgb(123,225,176)); using var clip = Round(24,8,15,10,4); g.FillPath(mint,clip);
        using var line = new Pen(Color.FromArgb(72,117,108),2.5f) { StartCap=LineCap.Round, EndCap=LineCap.Round };
        g.DrawLine(line,22,26,39,26); g.DrawLine(line,22,33,35,33); g.DrawLine(line,22,40,29,40);
        using var badge = new SolidBrush(Color.FromArgb(73,211,153)); g.FillEllipse(badge,35,35,25,25);
        using var plus = new Pen(Color.FromArgb(12,45,38),3.4f) { StartCap=LineCap.Round, EndCap=LineCap.Round }; g.DrawLine(plus,42,47.5f,53,47.5f); g.DrawLine(plus,47.5f,42,47.5f,53);
        return bitmap;
    }
    public static Icon Create()
    {
        using var bitmap = Render(64); var handle=bitmap.GetHicon();
        try { using var borrowed=Icon.FromHandle(handle); return (Icon)borrowed.Clone(); }
        finally { DestroyIcon(handle); }
    }
    internal static void Write(string path)
    {
        int[] sizes = [16,24,32,48,64,128,256]; var images=new List<byte[]>();
        foreach (int size in sizes) { using var bitmap=Render(size); using var stream=new MemoryStream(); bitmap.Save(stream,ImageFormat.Png); images.Add(stream.ToArray()); }
        using var writer=new BinaryWriter(File.Create(path)); writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)sizes.Length); int offset=6+16*sizes.Length;
        for(int i=0;i<sizes.Length;i++) { writer.Write((byte)(sizes[i]==256?0:sizes[i])); writer.Write((byte)(sizes[i]==256?0:sizes[i])); writer.Write((byte)0); writer.Write((byte)0); writer.Write((ushort)1); writer.Write((ushort)32); writer.Write(images[i].Length); writer.Write(offset); offset+=images[i].Length; }
        foreach(var bytes in images) writer.Write(bytes);
    }
}
