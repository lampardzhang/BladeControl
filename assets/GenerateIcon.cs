using System;
using System.IO;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
class GenerateIcon {
 static Bitmap Draw(int size) {
  Bitmap b=new Bitmap(size,size);using(Graphics g=Graphics.FromImage(b)) {
   g.SmoothingMode=SmoothingMode.AntiAlias;g.ScaleTransform(size/256f,size/256f);
   using(var bg=new SolidBrush(Color.FromArgb(19,30,37)))
   using(var path=new GraphicsPath()) {path.AddArc(8,8,64,64,180,90);path.AddArc(184,8,64,64,270,90);path.AddArc(184,184,64,64,0,90);path.AddArc(8,184,64,64,90,90);path.CloseFigure();g.FillPath(bg,path);}
   using(var pen=new Pen(Color.FromArgb(58,103,98),8)) g.DrawEllipse(pen,36,36,184,184);
   for(int i=0;i<3;i++) {
    var state=g.Save();g.TranslateTransform(128,128);g.RotateTransform(i*120);
    using(var path=new GraphicsPath()) {
     path.AddBezier(13,-14,27,-75,84,-92,74,-28);path.AddBezier(74,-28,63,9,35,23,13,13);path.CloseFigure();
     using(var fill=new LinearGradientBrush(new Rectangle(5,-82,85,115),Color.FromArgb(118,251,172),Color.FromArgb(35,199,177),60))g.FillPath(fill,path);
    }g.Restore(state);
   }
   using(var dark=new SolidBrush(Color.FromArgb(19,30,37)))g.FillEllipse(dark,102,102,52,52);
   using(var mint=new SolidBrush(Color.FromArgb(147,255,196)))g.FillEllipse(mint,113,113,30,30);
  }return b;
 }
 static void Main(string[] args) {
  int[] sizes={16,24,32,48,64,128,256};var images=new byte[sizes.Length][];
  for(int i=0;i<sizes.Length;i++)using(var b=Draw(sizes[i]))using(var m=new MemoryStream()){b.Save(m,ImageFormat.Png);images[i]=m.ToArray();}
  using(var w=new BinaryWriter(File.Create(args[0]))) {
   w.Write((ushort)0);w.Write((ushort)1);w.Write((ushort)sizes.Length);int offset=6+16*sizes.Length;
   for(int i=0;i<sizes.Length;i++){w.Write((byte)(sizes[i]==256?0:sizes[i]));w.Write((byte)(sizes[i]==256?0:sizes[i]));w.Write((ushort)0);w.Write((ushort)1);w.Write((ushort)32);w.Write(images[i].Length);w.Write(offset);offset+=images[i].Length;}
   foreach(var data in images)w.Write(data);
  }
  using(var preview=Draw(256))preview.Save(args[1],ImageFormat.Png);
 }
}
