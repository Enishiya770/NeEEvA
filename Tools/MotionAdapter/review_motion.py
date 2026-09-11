"""Paired motion diagnostics and an optional skeleton GIF for human review."""
import argparse
import json
from pathlib import Path

import numpy as np


def compare(teacher, student):
    for key in ['posed_joints','local_rot_mats','root_positions','foot_contacts']:
        if teacher[key].shape != student[key].shape:
            raise ValueError(f'Motion shape mismatch: {key}')
    if str(teacher['text']) != str(student['text']) or teacher['fps'] != student['fps']:
        raise ValueError('Motion description or frame rate mismatch')
    tj, sj = teacher['posed_joints'], student['posed_joints']
    root_difference = np.linalg.norm(teacher['root_positions']-student['root_positions'], axis=-1)
    aligned_difference = np.linalg.norm((tj-tj[:,:1])-(sj-sj[:,:1]), axis=-1)
    rt, rs = teacher['local_rot_mats'], student['local_rot_mats']
    cos_angle = (np.einsum('...ij,...ij->...',rt,rs)-1)/2
    return {'root_position_difference_m':float(root_difference.mean()),
            'root_aligned_joint_difference_m':float(aligned_difference.mean()),
            'local_rotation_difference_degrees':float(np.rad2deg(np.arccos(cos_angle.clip(-1,1))).mean()),
            'foot_contact_disagreement':float(np.mean((teacher['foot_contacts']>.5)!=(student['foot_contacts']>.5)))}


def render_pair(teacher, student, path):
    import matplotlib
    matplotlib.use('Agg')
    import matplotlib.pyplot as plt
    from matplotlib.animation import FuncAnimation, PillowWriter
    from matplotlib.ticker import MaxNLocator
    joints = [teacher['posed_joints'],student['posed_joints']]
    parents, names = teacher['joint_parents'], teacher['joint_names']
    if not np.array_equal(parents,student['joint_parents']):
        raise ValueError('Skeleton hierarchy mismatch')
    all_joints = np.concatenate(joints,axis=0)
    lower = all_joints.min(axis=(0,1))
    upper = all_joints.max(axis=(0,1))
    center = (upper+lower)/2
    half = max(float((upper-lower).max())*.56, .5)
    fig = plt.figure(figsize=(9,5.1),facecolor='white')
    artists = []
    for k,title in enumerate(['Original teacher condition','Qwen + adapter condition']):
        ax = fig.add_subplot(1,2,k+1,projection='3d')
        ax.set_title(title,fontsize=11)
        # ARDY is Y-up; matplotlib displays its vertical coordinate as Z.
        ax.set_xlim(center[0]-half,center[0]+half)
        ax.set_ylim(center[2]-half,center[2]+half)
        ax.set_zlim(center[1]-half,center[1]+half)
        ax.set_box_aspect((1,1,1))
        ax.view_init(elev=15,azim=-65)
        ax.set_xlabel('X (m)'); ax.set_ylabel('Z (m)'); ax.set_zlabel('Y (m)')
        for axis in [ax.xaxis,ax.yaxis,ax.zaxis]:
            axis.set_major_locator(MaxNLocator(nbins=3))
        ax.tick_params(labelsize=9)
        bones = []
        for i,parent in enumerate(parents):
            if parent < 0:
                continue
            color = '#bd5427' if 'Left' in str(names[i]) else '#2c718f' if 'Right' in str(names[i]) else '#444444'
            line, = ax.plot([],[],[],color=color,linewidth=2.5,marker='o',markersize=3)
            bones.append((i,int(parent),line))
        artists.append(bones)
    fig.suptitle(str(teacher['text']),fontsize=10,wrap=True)
    timer = fig.text(.5,.055,'',ha='center',fontsize=9)
    fig.text(.5,.018,'Orange: left limbs   |   Blue: right limbs',ha='center',fontsize=9)
    fig.subplots_adjust(left=.02,right=.97,bottom=.13,top=.85,wspace=.12)
    def update(frame):
        for values,bones in zip(joints,artists):
            for i,parent,line in bones:
                pair = values[frame,[parent,i]]
                line.set_data_3d(pair[:,0],pair[:,2],pair[:,1])
        timer.set_text(f'{frame/int(teacher["fps"]):.2f} s  |  raw ARDY skeleton; no retargeting or motion correction')
    animation = FuncAnimation(fig,update,frames=len(joints[0]),interval=1000/int(teacher['fps']))
    animation.save(path,writer=PillowWriter(fps=int(teacher['fps'])),dpi=90)
    update(len(joints[0])//2)
    fig.savefig(path.with_suffix('.png'),dpi=140)
    plt.close(fig)


def main():
    p = argparse.ArgumentParser()
    p.add_argument('--directory',type=Path,required=True)
    p.add_argument('--preview-id',help='Render this motion ID as a paired GIF and still image')
    args = p.parse_args()
    results = []
    for source in sorted(args.directory.glob('*-teacher.npz')):
        identifier = source.name.removesuffix('-teacher.npz')
        target = args.directory/f'{identifier}-student.npz'
        with np.load(source,allow_pickle=False) as t,np.load(target,allow_pickle=False) as s:
            results.append({'id':identifier,'text':str(t['text']),**compare(t,s)})
            if identifier == args.preview_id:
                render_pair(t,s,args.directory/f'{identifier}-comparison.gif')
    if not results:
        raise ValueError('No teacher/student clip pairs found')
    if args.preview_id and args.preview_id not in {r['id'] for r in results}:
        raise ValueError('Requested preview ID was not found')
    report = {'schema':1,'results':results,
              'limitation':'Same-frame distances without phase alignment measure trajectory differences, not naturalness or instruction success. Cold-start clips need not share an initial full-body pose, even with the same seed. Human review is required.'}
    (args.directory/'motion-differences.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
    print(f'Compared {len(results)} motion pairs')


if __name__ == '__main__':
    main()
