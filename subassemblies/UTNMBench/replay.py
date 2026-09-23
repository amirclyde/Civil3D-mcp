"""Replay the sections of a Civil 3D snapshot (bowtie_seam snapshotPath) through the offline evaluator."""
import json, math, sys
from simulate import Part
def load(n):
    d=json.load(open(n)); S=d['samples']; g=d['ground']
    def ground(x,y):
        fx=(x-g['x0'])/g['cell'];fy=(y-g['y0'])/g['cell'];i=int(math.floor(fx));j=int(math.floor(fy))
        if i<0 or j<0 or i>=g['nx']-1 or j>=g['ny']-1: return None
        z=[g['z'][b*g['nx']+a] for a,b in((i,j),(i+1,j),(i,j+1),(i+1,j+1))]
        if any(v is None for v in z): return None
        u=fx-i;v=fy-j
        return z[0]*(1-u)*(1-v)+z[1]*u*(1-v)+z[2]*(1-u)*v+z[3]*u*v
    def frame(s):
        i=min(range(len(S['s'])),key=lambda k:abs(S['s'][k]-s)); return S['x'][i],S['y'][i],S['dir'][i]
    return d,ground,frame
P=dict(MaxDaylightHeight=4.0,BenchHeight=4.0)
old,new=Part("UTNMBench-C3D25-DrainConnect.pkt"),Part("UTNMBench_v0.2.pkt")
for snap,sgn in ((sys.argv[1],-1),(sys.argv[2],1)):
    d,G,frame=load(snap)
    agree=0;n=0;gaps=[];tgaps=[];prop=0;rows=[]
    for sec in d['sections']:
        st=sec['station']; x,y,th=frame(st); nx,ny=(-math.sin(th)*sgn,math.cos(th)*sgn)
        g=lambda o,x=x,y=y,nx=nx,ny=ny,z0=sec['z0']:(None if G(x+nx*o,y+ny*o) is None else G(x+nx*o,y+ny*o)-z0)
        if g(0) is None: continue
        op,_,_=old.run(g,P); np_,_,_=new.run(g,P)
        n+=1
        # does the evaluator reproduce what Civil 3D built with the original part? (grid ground vs TIN: compare loosely)
        c3d=sec['template'][-1]; mine=op[-1]
        agree+=abs(c3d[0]-mine[1])<0.5 and len(sec['template'])>=len(op)-3
        real=[p for p in np_ if 'Proposed' not in p[3]]
        dl=[p for p in real if 'Daylight' in p[3]]
        if not dl:
            offgrid=globals().get('offgrid',0)+1; globals()['offgrid']=offgrid; continue
        end=dl[-1]
        gaps.append(abs(end[2]-g(end[1])))
        tb=[p for p in real if 'TBenchDrainIn' in p[3]]
        if tb: tgaps.append(abs(tb[-1][2]-g(tb[-1][1])))
        prop+=any('Proposed' in p[3] for p in np_)
        rows.append((st,op[-1][1],end[1],tgaps[-1] if tb else float('nan')))
    tg=sorted(tgaps); print("   sections running off the snapshot's ground grid (skipped):",globals().get("offgrid",0)); globals()["offgrid"]=0
    print(f"{snap}: {n} sections; evaluator agrees with Civil 3D (original part, reach within 0.5 m) at {agree}")
    print(f"   v0.2: all end on the ground: {max(gaps)<1e-6};  end of the terminal bench to ground: median {tg[len(tg)//2]:.3f}, 90% {tg[int(.9*len(tg))]:.3f}, worst {tg[-1]:.3f} m;  marked Proposed: {prop}")
    if '-v' in sys.argv:
        for r in rows: print("   %7.1f  reach orig %6.2f  v0.2 %6.2f   tdrain-ground %.3f"%r)
