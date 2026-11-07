using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SamaraLauncher.App.Controls;

public partial class PixelPanorama
{
    // Artwork and overlays share an 800 x 500 reference grid.
    // Masks keep sky effects behind the terrain and reflections inside the lake.
    private const int SceneW = 800, SceneH = 500;
    private sealed record SceneCloud(BitmapSource Image, double X, double Y, double Speed, double Width, double Height);
    private static readonly SolidColorBrush[] SceneColors = MakeSceneColors();
    private static readonly BitmapSource SceneSky = LoadValleyPlate();
    private static readonly Geometry SkyMask = MakeMask("M 0,0 L 800,0 800,190 755,185 706,195 670,185 621,199 581,204 523,185 466,206 405,185 371,179 326,157 290,157 236,120 212,142 180,143 144,159 117,146 94,164 75,171 65,128 43,100 29,57 15,92 0,98 Z");
    private static readonly Geometry WaterMask = MakeMask("M 216,339 L 697,339 744,344 785,347 800,350 800,440 764,443 733,467 716,500 235,500 218,479 180,462 162,452 128,447 118,426 98,412 90,395 69,384 80,374 132,366 177,362 214,357 Z");
    private static Geometry MakeMask(string path)
    {
        var geometry = Geometry.Parse(path);
        geometry.Freeze();
        return geometry;
    }
    private static BitmapSource LoadValleyPlate()
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = new Uri("pack://application:,,,/Assets/NightValley-clean-v1.png");
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static readonly SolidColorBrush[] ReferenceStarColors = [
        PixelPalette.CreateBrush(Color.FromRgb(76,147,240)),
        PixelPalette.CreateBrush(Color.FromRgb(126,186,255)),
        PixelPalette.CreateBrush(Color.FromRgb(210,227,255)),
        PixelPalette.CreateBrush(Color.FromRgb(97,163,250)),
        PixelPalette.CreateBrush(Color.FromRgb(239,243,255))
    ];

    private static SolidColorBrush[] MakeSceneColors()
    {
        string[] colors=["#04132D","#071E40","#103568","#234C83","#3C6AA2","#6D98C6","#B8D4E8","#EEF4E7",
            "#071E30","#0B2D3B","#14434A","#245B55","#3B6C58","#102A4A","#163B63","#20507C",
            "#142535","#253845","#405365","#6B7A85","#332B30","#574038","#79563D","#A47B4B",
            "#E5AC50","#FFE395","#203257","#334A70","#506B93","#121E33"];
        var brushes=new SolidColorBrush[colors.Length];
        for(int i=0;i<colors.Length;i++){brushes[i]=new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));brushes[i].Freeze();}
        return brushes;
    }
    private static double Noise(int a,int b=0)
    {
        unchecked {uint n=(uint)(a*374761393+b*668265263+1274126177);n=(n^(n>>13))*1274126177;return (n^(n>>16))/(double)uint.MaxValue;}
    }
    private static void Block(DrawingContext dc,int color,double x,double y,double w,double h)
        =>dc.DrawRectangle(SceneColors[color],null,new Rect(Math.Floor(x),Math.Floor(y),Math.Max(1,Math.Ceiling(w)),Math.Max(1,Math.Ceiling(h))));
    private static void Shape(DrawingContext dc,int color,params double[] coords)
    {
        var g=new StreamGeometry();using(var c=g.Open()){
            c.BeginFigure(new Point(Math.Round(coords[0]),Math.Round(coords[1])),true,true);
            for(int i=2;i<coords.Length;i+=2)c.LineTo(new Point(Math.Round(coords[i]),Math.Round(coords[i+1])),true,false);
        }g.Freeze();dc.DrawGeometry(SceneColors[color],null,g);
    }
    private static BitmapSource Bake(Action<DrawingContext> paint,int width=SceneW,int height=SceneH)
    {
        var visual=new DrawingVisual();RenderOptions.SetEdgeMode(visual,EdgeMode.Aliased);
        using(var dc=visual.RenderOpen())paint(dc);
        var image=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);image.Render(visual);image.Freeze();return image;
    }
    private static BitmapSource PaintScene(bool terrain)=>Bake(dc=>
    {
        if(!terrain)
        {
            for(int y=0;y<SceneH;y++)
            {
                double t=Math.Clamp(y/330d,0,1);
                var c=Color.FromRgb((byte)(4+9*t),(byte)(16+25*t),(byte)(39+43*t));
                var brush=new SolidColorBrush(c);brush.Freeze();dc.DrawRectangle(brush,null,new Rect(0,y,SceneW,1));
            }
            return;
        }
        dc.DrawImage(MountainPainting(),new Rect(0,0,800,350));
        ForestPainting(dc);
        // Water opens into the lower centre; shores are kept in a separate foreground.
        for(int y=337;y<SceneH;y++)
        {
            double t=(y-337)/163d;var brush=new SolidColorBrush(Color.FromRgb((byte)(5+2*t),(byte)(23-5*t),(byte)(49-12*t)));brush.Freeze();
            dc.DrawRectangle(brush,null,new Rect(0,y,SceneW,1));
        }
        for(int i=0;i<230;i++)
        {
            double y=339+Noise(i,73)*150,x=Noise(i,74)*800;
            dc.PushOpacity(.13+Noise(i,75)*.18);Block(dc,3,x,y,2+Noise(i,76)*18,1);dc.Pop();
        }
        Shape(dc,8,0,343,58,351,99,362,147,377,204,390,246,409,312,461,360,500,0,500);
        Shape(dc,10,0,353,66,360,112,374,157,382,204,399,153,394,101,382,48,373,0,369);
        Shape(dc,8,800,336,771,352,747,368,720,407,682,444,654,500,800,500);
        Shape(dc,10,800,373,773,381,748,406,723,435,704,458,742,425,779,406,800,398);
        for(int i=0;i<2100;i++)
        {
            double x=Noise(i,81)*800,y=359+Noise(i,82)*141;
            bool shore=x<Math.Max(0,(y-350)*2.25)||x>800-Math.Max(0,(y-330)*.85);
            if(!shore)continue;
            Block(dc,i%5==0?12:10,x,y,1+Noise(i,83)*3,1+Noise(i,84)*2);
            if(i%9==0){Block(dc,17,x,y+3,9,5);Block(dc,18,x,y+3,6,2);}
        }
        // Modest cabin: the chimney coordinate is shared with the animated smoke.
        Block(dc,20,91,354,47,28);Block(dc,21,94,356,40,24);
        for(int y=358;y<380;y+=4)Block(dc,20,94,y,40,1);
        Shape(dc,26,84,354,112,332,143,355,137,359,111,340,91,359);
        Shape(dc,18,87,352,112,333,135,351,128,348,111,338,96,351);
        for(int i=0;i<5;i++)Block(dc,27,94-i*2,350+i*2,35+i*3,1);
        Block(dc,22,119,360,11,21);Block(dc,24,99,361,12,11);Block(dc,25,101,362,8,8);
        Block(dc,20,104,360,1,12);Block(dc,20,99,366,12,1);
        Block(dc,24,122,362,4,6);Block(dc,18,106,329,5,13);Block(dc,19,105,328,7,3);
        Block(dc,23,92,381,44,2);
        // Large framing spruce, with uneven branch lengths and a visible trunk.
        Pine(dc,35,383,263,8,791);Pine(dc,776,403,182,8,793);
        for(int i=0;i<18;i++){double x=160+Noise(i,93)*80;Pine(dc,x,385+Noise(i,94)*33,18+Noise(i,95)*43,8,i+900);}
        for(int i=0;i<12;i++){double x=725+Noise(i,96)*62;Pine(dc,x,420+Noise(i,97)*35,32+Noise(i,98)*65,8,i+950);}
    });
    private static void NativeRidge(DrawingContext dc,int color,double baseY,double amplitude,int seed)
    {
        var points=new double[2*(83+2)];points[0]=0;points[1]=500;
        for(int i=0;i<83;i++){points[(i+1)*2]=i*10;points[(i+1)*2+1]=baseY+Math.Sin(i*.17+seed)*amplitude+Noise(i,seed)*5;}
        points[^2]=820;points[^1]=500;Shape(dc,color,points);
    }
    private static void Pine(DrawingContext dc,double x,double ground,double height,int color,int seed)
    {
        int h=Math.Max(8,(int)height),w=Math.Max(7,(int)(height*.52));
        var sprite=PinePainting(w,h,seed,color);
        dc.DrawImage(sprite,new Rect(Math.Round(x-w*.5),Math.Round(ground-h),w,h));
    }    private static SceneCloud[] MakeSceneClouds()
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = new Uri("pack://application:,,,/Assets/ReferenceCloud-v1.png");
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.EndInit();
        image.Freeze();
        var atlas = new BitmapImage();
        atlas.BeginInit();
        atlas.UriSource = new Uri("pack://application:,,,/Assets/ReferenceClouds-atlas-v1.png");
        atlas.CacheOption = BitmapCacheOption.OnLoad;
        atlas.EndInit();
        atlas.Freeze();
        BitmapSource Cell(int col,int row)
        {
            int w=atlas.PixelWidth/2,h=atlas.PixelHeight/2;
            var cell=new CroppedBitmap(atlas,new Int32Rect(col*w,row*h,w,h));
            var rgba=new FormatConvertedBitmap(cell,PixelFormats.Bgra32,null,0);
            var pixels=new byte[w*h*4];
            rgba.CopyPixels(pixels,w*4,0);
            int left=w,top=h,right=0,bottom=0;
            for(int y=0;y<h;y++)for(int x=0;x<w;x++)
            {
                if(pixels[(y*w+x)*4+3]<16)continue;
                left=Math.Min(left,x);right=Math.Max(right,x);
                top=Math.Min(top,y);bottom=Math.Max(bottom,y);
            }
            var trimmed=new CroppedBitmap(cell,new Int32Rect(left,top,right-left+1,bottom-top+1));
            trimmed.Freeze();return trimmed;
        }
        SceneCloud Cloud(BitmapSource sprite,double x,double y,double speed,double width)
            =>new(sprite,x,y,speed,width,width*sprite.PixelHeight/sprite.PixelWidth);
        return [
            Cloud(Cell(0,0),-6,30,.38,234),
            Cloud(image,236,92,.53,192),
            Cloud(Cell(0,1),692,69,.32,181),
            Cloud(Cell(1,0),635,119,.27,106),
            Cloud(Cell(1,1),570,139,.22,82)
        ];
    }
    private void DrawNightValley(DrawingContext dc,double width,double height)
    {
        double scale=Math.Max(width/SceneW,height/SceneH);
        dc.PushClip(new RectangleGeometry(new Rect(0,0,width,height)));
        dc.PushTransform(new TranslateTransform((width-SceneW*scale)/2,(height-SceneH*scale)/2));
        dc.PushTransform(new ScaleTransform(scale,scale));
        RenderOptions.SetBitmapScalingMode(this,BitmapScalingMode.NearestNeighbor);
        dc.DrawImage(SceneSky,new Rect(0,0,SceneW,SceneH));
        dc.PushClip(SkyMask);
        for(int i=0;i<560;i++)
        {
            double x=Noise(i,1)*800,y=Noise(i,2)*220;
            double twinkle=.18+.82*Math.Pow(.5+.5*Math.Sin(_time*(.8+Noise(i,3)*1.6)+i*2.3),2);
                        dc.PushOpacity(twinkle*(.75+Noise(i,4)*.25));
            double size=i%37==0?1.8:i%11==0?1.25:.8;
            dc.DrawRectangle(ReferenceStarColors[i%ReferenceStarColors.Length],null,new Rect(Math.Floor(x*2)/2,Math.Floor(y*2)/2,size,size));
            dc.Pop();
            if(i%73==0){dc.PushOpacity(twinkle*.75);Block(dc,6,x-1,y,3,1);Block(dc,6,x,y-1,1,3);dc.Pop();}
        }
        for(int i=4;i>=1;i--){dc.PushOpacity(.018*(5-i));Block(dc,5,650-i*5,81-i*5,27+i*10,27+i*10);dc.Pop();}
        Block(dc,7,650,81,27,27);Block(dc,6,652,83,23,23);
        for(int i=0;i<14;i++)Block(dc,i%3==0?7:5,653+Noise(i,11)*19,84+Noise(i,12)*19,2+Noise(i,13)*4,2+Noise(i,14)*3);
        dc.Pop();
        dc.PushClip(WaterMask);
        // Reflect actual terrain in horizontal strips. Wave displacement grows with
        // distance from the shoreline; the frozen source is reused on every frame.
        const double horizon=332;
        for(int row=0;row<84;row++)
        {
            double y=horizon+row*2,depth=row/84d;
            double shift=Math.Sin(row*.46+_time*.85)*(0.4+depth*2.8)
                +Math.Sin(row*.19-_time*.52)*depth*1.4;
            dc.PushClip(new RectangleGeometry(new Rect(0,y,800,2)));
            dc.PushOpacity((.27-depth*.15)*(.88+.12*Math.Sin(row*.57+_time*.6)));
            dc.PushTransform(new TranslateTransform(shift,horizon*1.72));
            dc.PushTransform(new ScaleTransform(1,-.72));
            dc.DrawImage(SceneSky,new Rect(0,0,800,500));
            dc.Pop();dc.Pop();dc.Pop();dc.Pop();
        }
        for(int i=0;i<210;i++)
        {
            double depth=Noise(i,103),y=334+depth*166;
            double pulse=Math.Pow(.5+.5*Math.Sin(_time*(.55+Noise(i,105)*.45)+i*1.7),3);
            double x=Noise(i,104)*800+Math.Sin(_time*.5+i)*(1+depth*4);
            dc.PushOpacity(.035+pulse*.24);
            Block(dc, i%7==0?5:3,x,y,2+depth*Noise(i,106)*17,1);
            dc.Pop();
        }
        DrawLakeLight(dc,115,359,52,1.0);
        DrawLakeLight(dc,662,333,29,.55);
        DrawLakeLight(dc,688,333,39,.65);
        for(int i=0;i<65;i++)
        {
            double depth=i/65d;
            double pulse=.3+.7*Math.Pow(.5+.5*Math.Sin(_time*.9+i*1.77),2);
            double spread=3+depth*34;
            double x=663+(Noise(i,155)-.5)*spread+Math.Sin(_time*.6+i)*2;
            dc.PushOpacity(pulse*(.12+depth*.08));
            Block(dc,6,x,335+i*2.5,2+Noise(i,156)*(3+depth*9),1);
            dc.Pop();
        }
        dc.Pop();
        if(ParticlesEnabled)
        {
            for(int i=0;i<13;i++)
            {
                double t=(_time/8+i/13d)%1;
                double x=110+t*12+Math.Sin(_time*.4+i)*t*3,y=294-t*35,size=1.4+t*7;
                dc.PushOpacity(Math.Min(1,t*15)*Math.Pow(1-t,1.7)*.36);
                Block(dc,6,x-size*.5,y,size,size*.6);Block(dc,6,x-size*.2,y-1,size*.6,size);dc.Pop();
            }
            for(int i=0;i<9;i++)
            {
                double pulse=Math.Pow(.5+.5*Math.Sin(_time*1.3+i*2.4),5);
                dc.PushOpacity(pulse*.65);Block(dc,25,65+Noise(i,121)*115+Math.Sin(_time*.3+i)*5,344+Noise(i,122)*19+Math.Sin(_time*.6+i)*3,1,1);dc.Pop();
            }
        }
        dc.Pop();dc.Pop();dc.Pop();
    }
    private void DrawLakeLight(DrawingContext dc,double x,double y,int rows,double strength)
    {
        for(int i=0;i<rows;i++)
        {
            double depth=i/(double)rows;
            double wave=Math.Sin(_time*1.1+i*.95);
            double spread=2+depth*8;
            double pulse=.35+.65*(.5+.5*Math.Sin(_time*1.4+i*1.67));
            dc.PushOpacity(strength*(1-depth)*pulse*.85);
            Block(dc,i%4==0?25:24,x+wave*spread-spread*.5,y+i*1.2,2+Noise(i,110)*spread,1);
            dc.Pop();
        }
    }}








