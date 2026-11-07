using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
namespace SamaraLauncher.App.Controls;
public partial class PixelPanorama
{
    private static double SmoothNoise(double x,double y,int seed)
    {
        int ix=(int)Math.Floor(x),iy=(int)Math.Floor(y);double tx=x-ix,ty=y-iy;
        tx=tx*tx*(3-2*tx);ty=ty*ty*(3-2*ty);
        double a=Noise(ix+iy*901,seed),b=Noise(ix+1+iy*901,seed),c=Noise(ix+(iy+1)*901,seed),d=Noise(ix+1+(iy+1)*901,seed);
        return (a*(1-tx)+b*tx)*(1-ty)+(c*(1-tx)+d*tx)*ty;
    }
    private static BitmapSource Pixels(int w,int h,Func<int,int,(double R,double G,double B,double A)> paint)
    {
        var data=new byte[w*h*4];for(int y=0;y<h;y++)for(int x=0;x<w;x++)
        {
            var p=paint(x,y);int k=(y*w+x)*4;data[k]=(byte)Math.Clamp(p.B,0,255);data[k+1]=(byte)Math.Clamp(p.G,0,255);data[k+2]=(byte)Math.Clamp(p.R,0,255);data[k+3]=(byte)Math.Clamp(p.A,0,255);
        }
        var bmp=BitmapSource.Create(w,h,96,96,PixelFormats.Bgra32,null,data,w*4);bmp.Freeze();return bmp;
    }
    private static BitmapSource MountainPainting()
    {
        double[] xs=[0,63,92,151,177,239,265,315,341,398,443,492,524,563,604,650,693,741,800];
        double[] ys=[249,218,229,185,201,141,169,181,201,239,217,242,217,239,229,207,231,225,250];
        double Outline(double x){int i=0;while(i<xs.Length-2&&x>xs[i+1])i++;double t=(x-xs[i])/(xs[i+1]-xs[i]);return ys[i]*(1-t)+ys[i+1]*t;}
        return Pixels(800,350,(x,y)=>
        {
            double edge=Outline(x)+(SmoothNoise(x*.13,0,8)-.5)*3;
            if(y<edge)return(0,0,0,0);
            double depth=y-edge;
            double n=SmoothNoise(x*.055,y*.065,91)*.5+SmoothNoise(x*.16,y*.14,92)*.28+SmoothNoise(x*.5,y*.45,93)*.14+Noise(x+y*800,94)*.08;
            double peak=x<205?151:x<300?239:x<390?315:x<480?443:x<570?524:x<625?604:x<720?650:741;
            double dx=x-peak;
            double folded=(Math.Sin(dx*.23+(y-Outline(peak))*.12+SmoothNoise(x*.025,y*.028,76)*7)*.5+.5);
            double vein=SmoothNoise((x+(y-edge)*.5)*.13,y*.018,24);
            double light=Math.Clamp(.23+n*.73+folded*.18+(dx<0?.12:-.10),0,1);
            double snowline=17+Math.Max(0,190-Outline(peak))*.5;
            double snowLimit=snowline+SmoothNoise(x*.15,y*.06,82)*18+(vein>.6?22:0);
            bool snow=depth<snowLimit&&(vein>.29||depth<5)&&edge<236;
            double haze=Math.Clamp((y-220)/110d,0,.5);
            double r,g,b;
            if(snow){r=61+light*91;g=100+light*89;b=150+light*72;}
            else{r=9+light*28;g=30+light*44;b=69+light*64;}
            if(vein<.22&&!snow){r*=.8;g*=.8;b*=.85;}
            return(r*(1-haze)+19*haze,g*(1-haze)+52*haze,b*(1-haze)+93*haze,255);
        });
    }
    private static void ForestPainting(DrawingContext dc)
    {
        for(int layer=0;layer<4;layer++)
        {
            int l=layer;
            double Ground(double x)=>255+l*24+Math.Sin(x*.011+l*1.7)*14+Math.Sin(x*.027+l)*6+SmoothNoise(x*.018,0,l)*8;
            var hills=Pixels(800,350,(x,y)=>
            {
                double top=Ground(x);if(y<top)return(0,0,0,0);
                double n=SmoothNoise(x*.22,y*.24,l)*.55+Noise(x+y*800,l+66)*.45;
                double mist=Math.Exp(-Math.Pow((y-top-7)/13,2))*(3-l)*.1;
                return(4+(3-l)*3+n*3+mist*10,17+(3-l)*6+n*6+mist*25,27+(3-l)*14+n*9+mist*40,255);
            });dc.DrawImage(hills,new Rect(0,0,800,350));
            // Sort by depth so crowns overlap like a forest, with small clearings.
            for(int row=0;row<3;row++)for(int i=0;i<175;i++)
            {
                double x=Noise(i,l*13+row+77)*800;
                if(Noise(i,321+l)<.10)continue;
                double y=Ground(x)+row*9+4;
                Pine(dc,x,y,8+l*3+Noise(i,66+row)*13,l==0?14:l==1?13:8,i+row*800+l*6000);
            }
        }
    }
    private static BitmapSource PinePainting(int w,int h,int seed,int color)
    {
        return Pixels(w,h,(x,y)=>
        {
            double t=y/(double)h,cx=w*.5+(SmoothNoise(y*.08,0,seed)-.5)*w*.05;
            double branch=(Math.Sin(t*72+seed)*.5+.5);
            double width=w*.5*t*(.45+.55*branch)*(.8+SmoothNoise(y*.2,0,seed+1)*.2);
            double n=SmoothNoise(x*.46,y*.44,seed)*.6+Noise(x+y*w,seed)*.4;
            bool trunk=Math.Abs(x-cx)<Math.Max(.6,w*.025)&&y>h*.15;
            if(Math.Abs(x-cx)>width&&!trunk)return(0,0,0,0);
            if(y>h*.92&&!trunk)return(0,0,0,0);
            if(trunk&&y>h*.78)return(39+n*20,32+n*15,27+n*12,255);
            double side=(cx-x)/Math.Max(1,width)*.15;
            double shine=(branch>.82?.14:0)+n*.5+side;
            double distance=color==14?.9:color==13?.6:0;
            return(3+shine*18+distance*9,17+shine*40+distance*13,23+shine*31+distance*35,255);
        });
    }
    private static BitmapSource CloudPainting(int w,int h,int seed)
    {
        return Pixels(w,h,(x,y)=>
        {
            double density=0,shade=0;
            // A tall asymmetric crown, lower satellite lobes and a trailing wisp.
            // Each lobe shades itself; the lit rim faces the moon at upper right.
            for(int j=0;j<12;j++)
            {
                double u=j/11d;
                double crown=Math.Exp(-Math.Pow((u-.43)/.25,2));
                double cx=w*(.09+u*.80),cy=h*(.76-crown*.34+(Noise(j,seed+61)-.5)*.15);
                double rx=w*(.065+Noise(j,seed+62)*.055),ry=h*(.10+crown*.27+Noise(j,seed+63)*.12);
                double dx=(x-cx)/rx,dy=(y-cy)/ry,d=1-dx*dx-dy*dy;
                if(d>density){density=d;shade=Math.Clamp(.43-dy*.46+dx*.24,0,1);}
            }
            if(density<=0)return(0,0,0,0);
            double n=SmoothNoise(x*.12,y*.14,seed+64)-.5;
            double edge=Math.Clamp(density*18,0,1);
            double light=Math.Round(Math.Clamp(shade+n*.14,0,1)*8)/8;
            return(11+light*46,29+light*65,69+light*100,245*edge);
        });
    }
}
