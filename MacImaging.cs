using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Color=System.Drawing.Color;
using Rectangle=System.Drawing.Rectangle;

// macOS stand-ins for the System.Drawing calls the macro makes; System.Drawing's Bitmap only runs on Windows.
// Pixels are kept as packed RGB bytes decoded straight from the PNG data (no color management), so GetPixel gives
// the same R, G and B that GDI+ gives on Windows. Screenshots and templates are 8-bit, non-interlaced PNGs.
sealed class Bitmap : IDisposable, ICloneable
{
    readonly byte[] pixels;
    public int Width { get; }
    public int Height { get; }
    public PixelFormat PixelFormat=>PixelFormat.Format24bppRgb;

    Bitmap(int width,int height,byte[] pixels) { Width=width; Height=height; this.pixels=pixels; }
    // Like GDI+, a new bitmap starts black.
    public Bitmap(int width,int height):this(width,height,new byte[width*height*3]) { }
    public Bitmap(Bitmap source):this(source.Width,source.Height,(byte[])source.pixels.Clone()) { }
    public Bitmap(string path):this(Png.Decode(File.ReadAllBytes(path))) { }
    public Bitmap(Stream stream):this(Png.Decode(ReadAll(stream))) { }
    Bitmap((int Width,int Height,byte[] Pixels) decoded):this(decoded.Width,decoded.Height,decoded.Pixels) { }
    static byte[] ReadAll(Stream stream) { using var copy=new MemoryStream(); stream.CopyTo(copy); return copy.ToArray(); }

    int Offset(int x,int y)
    {
        if((uint)x>=(uint)Width || (uint)y>=(uint)Height) throw new ArgumentOutOfRangeException(nameof(x),$"({x},{y}) is outside {Width}x{Height}.");
        return (y*Width+x)*3;
    }
    public Color GetPixel(int x,int y) { var i=Offset(x,y); return Color.FromArgb(pixels[i],pixels[i+1],pixels[i+2]); }

    public Bitmap Clone(Rectangle area,PixelFormat format)
    {
        if(area.X<0 || area.Y<0 || area.Width<=0 || area.Height<=0 || area.Right>Width || area.Bottom>Height) throw new ArgumentOutOfRangeException(nameof(area));
        var copy=new Bitmap(area.Width,area.Height);
        for(var y=0;y<area.Height;y++) Array.Copy(pixels,Offset(area.X,area.Y+y),copy.pixels,y*area.Width*3,area.Width*3);
        return copy;
    }
    public object Clone()=>new Bitmap(this);

    internal void Fill(Rectangle area,Color color)
    {
        area.Intersect(new Rectangle(0,0,Width,Height));
        for(var y=area.Top;y<area.Bottom;y++) for(var x=area.Left;x<area.Right;x++)
        { var i=Offset(x,y); pixels[i]=color.R; pixels[i+1]=color.G; pixels[i+2]=color.B; }
    }
    internal void CopyFrom(Bitmap source,Rectangle area,System.Drawing.Point to)
    {
        for(var y=0;y<area.Height;y++) Array.Copy(source.pixels,source.Offset(area.X,area.Y+y),pixels,Offset(to.X,to.Y+y),area.Width*3);
    }

    public void Save(string path,ImageFormat format)=>Png.Encode(path,Width,Height,pixels);
    public void Dispose() { }
}

static class Png
{
    static readonly byte[] Signature=[137,80,78,71,13,10,26,10];

    // 8-bit grayscale, RGB, palette, gray+alpha and RGBA without interlacing, returned as RGB (alpha is dropped).
    public static (int Width,int Height,byte[] Rgb) Decode(byte[] data)
    {
        if(data.Length<8 || !data.AsSpan(0,8).SequenceEqual(Signature)) throw new InvalidDataException("Not a PNG image.");
        int width=0,height=0,depth=0,type=-1,interlace=0; byte[]? palette=null;
        using var compressed=new MemoryStream();
        for(var p=8;p+12<=data.Length;)
        {
            var length=BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(p));
            if(length<0 || p+12+length>data.Length) throw new InvalidDataException("Truncated PNG chunk.");
            var kind=Encoding.ASCII.GetString(data,p+4,4); var body=data.AsSpan(p+8,length);
            if(kind=="IHDR") { width=BinaryPrimitives.ReadInt32BigEndian(body); height=BinaryPrimitives.ReadInt32BigEndian(body[4..]); depth=body[8]; type=body[9]; interlace=body[12]; }
            else if(kind=="PLTE") palette=body.ToArray();
            else if(kind=="IDAT") compressed.Write(body);
            else if(kind=="IEND") break;
            p+=12+length;
        }
        var channels=type switch { 0=>1,2=>3,3=>1,4=>2,6=>4,_=>0 };
        if(depth!=8 || interlace!=0 || channels==0 || width<=0 || height<=0 || type==3 && palette==null)
            throw new NotSupportedException($"Unsupported PNG (bit depth {depth}, color type {type}, interlace {interlace}).");
        var stride=width*channels;
        var filtered=new byte[(stride+1)*height];
        compressed.Position=0;
        using(var zlib=new ZLibStream(compressed,CompressionMode.Decompress)) zlib.ReadExactly(filtered);
        var rows=new byte[stride*height];
        for(var y=0;y<height;y++)
        {
            var filter=filtered[y*(stride+1)];
            var source=filtered.AsSpan(y*(stride+1)+1,stride);
            var row=rows.AsSpan(y*stride,stride);
            ReadOnlySpan<byte> above=y>0?rows.AsSpan((y-1)*stride,stride):new byte[stride];
            for(var i=0;i<stride;i++)
            {
                int a=i>=channels?row[i-channels]:0,b=above[i],c=i>=channels?above[i-channels]:0;
                var predicted=filter switch { 0=>0,1=>a,2=>b,3=>(a+b)/2,4=>Paeth(a,b,c),_=>throw new InvalidDataException("Bad PNG filter.") };
                row[i]=(byte)(source[i]+predicted);
            }
        }
        var rgb=new byte[width*height*3];
        for(int i=0,o=0;i<rows.Length;i+=channels,o+=3)
            switch(type)
            {
                case 0: case 4: rgb[o]=rgb[o+1]=rgb[o+2]=rows[i]; break;
                case 3: palette.AsSpan(rows[i]*3,3).CopyTo(rgb.AsSpan(o,3)); break;
                default: rgb[o]=rows[i]; rgb[o+1]=rows[i+1]; rgb[o+2]=rows[i+2]; break;
            }
        return (width,height,rgb);
    }
    static int Paeth(int a,int b,int c)
    {
        int p=a+b-c,pa=Math.Abs(p-a),pb=Math.Abs(p-b),pc=Math.Abs(p-c);
        return pa<=pb && pa<=pc?a:pb<=pc?b:c;
    }

    // 8-bit RGB, unfiltered rows.
    public static void Encode(string path,int width,int height,byte[] rgb)
    {
        var header=new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header,width); BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4),height);
        header[8]=8; header[9]=2;
        using var compressed=new MemoryStream();
        using(var zlib=new ZLibStream(compressed,CompressionLevel.Fastest,true))
            for(var y=0;y<height;y++) { zlib.WriteByte(0); zlib.Write(rgb,y*width*3,width*3); }
        using var file=File.Create(path);
        file.Write(Signature);
        WriteChunk(file,"IHDR",header);
        WriteChunk(file,"IDAT",compressed.ToArray());
        WriteChunk(file,"IEND",[]);
    }
    static void WriteChunk(Stream stream,string kind,byte[] body)
    {
        Span<byte> number=stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number,body.Length); stream.Write(number);
        var type=Encoding.ASCII.GetBytes(kind); stream.Write(type); stream.Write(body);
        BinaryPrimitives.WriteUInt32BigEndian(number,Crc(type,body)); stream.Write(number);
    }
    static readonly uint[] CrcTable=Enumerable.Range(0,256).Select(n=>{ var c=(uint)n; for(var k=0;k<8;k++) c=(c&1)!=0?0xEDB88320^(c>>1):c>>1; return c; }).ToArray();
    static uint Crc(byte[] type,byte[] body)
    {
        var c=0xFFFFFFFFu;
        foreach(var b in type) c=CrcTable[(c^b)&0xFF]^(c>>8);
        foreach(var b in body) c=CrcTable[(c^b)&0xFF]^(c>>8);
        return c^0xFFFFFFFFu;
    }
}

enum PixelFormat { Format24bppRgb }
enum GraphicsUnit { Pixel=2 }
sealed class ImageFormat { public static ImageFormat Png { get; }=new(); }
sealed class Brush(Color color) { public Color Color { get; }=color; }
static class Brushes { public static Brush Black { get; }=new(Color.Black); }

// Only what the reward and receipt checks draw: filled rectangles and same-size copies.
sealed class Graphics : IDisposable
{
    readonly Bitmap target;
    Graphics(Bitmap target)=>this.target=target;
    public static Graphics FromImage(Bitmap image)=>new(image);
    public void FillRectangle(Brush brush,int x,int y,int width,int height)=>target.Fill(new Rectangle(x,y,width,height),brush.Color);
    public void DrawImage(Bitmap image,Rectangle destination,Rectangle source,GraphicsUnit unit)
    {
        if(destination.Size!=source.Size) throw new NotSupportedException("Only same-size copies are supported.");
        target.CopyFrom(image,source,destination.Location);
    }
    public void Dispose() { }
}
