"""Render the studio's restricted, data-only scene manifest with Blender Cycles.

Mirrors web/scene.ts: devices, finishes, stages, lighting moods, camera keys and easing, contain-fitted screens and
exploded screen sections (same motion curve as layerMotion in web/project.ts). Titles are not drawn here; Studio
composites the editor's own title layers afterwards. Callouts, focus regions and fades are refused before export.
Camera and layer motion are keyframed on every frame so motion blur is real.
"""
import bpy
import json
import math
import os
import sys
from mathutils import Vector

manifest, output = sys.argv[sys.argv.index('--') + 1:]
manifest = os.path.abspath(manifest)
output = os.path.abspath(output)
os.makedirs(output, exist_ok=True)
with open(manifest, encoding='utf-8') as handle:
    project = json.load(handle)
assets = {a['id']: a for a in project['assets']}
fps = project['fps']
frames = math.ceil(sum(s['duration'] for s in project['shots']) * fps - 1e-6)
FINISH = {'silver': ('#b9bdc3', .7, .28), 'graphite': ('#4a4d53', .65, .32), 'black': ('#17181b', .45, .38), 'white': ('#e9e8e4', .1, .42)}
screen_bindings = []

def clamp(v, low=0.0, high=1.0):
    return max(low, min(high, v))

def vec(value):
    return Vector((value[0], -value[2], value[1]))

def rgb(value):
    channels = [int(value[i:i+2], 16) / 255 for i in (1, 3, 5)]
    return tuple(c / 12.92 if c <= .04045 else ((c + .055) / 1.055) ** 2.4 for c in channels)

def physical(name, color, metal=.6, rough=.26, emission=0):
    material = bpy.data.materials.new(name)
    material.use_nodes = True
    shader = material.node_tree.nodes.get('Principled BSDF')
    shader.inputs['Base Color'].default_value = (*rgb(color), 1)
    shader.inputs['Metallic'].default_value = metal
    shader.inputs['Roughness'].default_value = rough
    if emission:
        shader.inputs['Emission Color'].default_value = (*rgb(color), 1)
        shader.inputs['Emission Strength'].default_value = emission
    return material

def box(name, width, height, depth, material, position, radius=.05, parent=None):
    bpy.ops.mesh.primitive_cube_add(size=1, location=(0, 0, 0))
    obj = bpy.context.object
    obj.name = name
    obj.dimensions = (width, depth, height)
    bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)
    bevel = obj.modifiers.new('Soft edges', 'BEVEL')
    bevel.width = min(radius, width / 2.01, height / 2.01, depth / 2.01)
    bevel.segments = 4
    obj.modifiers.new('Surface normals', 'WEIGHTED_NORMAL')
    obj.data.materials.append(material if not isinstance(material, str) else physical(name, material))
    obj.parent = parent
    obj.location = vec(position)
    return obj

def empty(name, position, parent=None):
    obj = bpy.data.objects.new(name, None)
    bpy.context.collection.objects.link(obj)
    obj.parent = parent
    obj.location = vec(position)
    return obj

def media_path(asset, local, shot):
    if asset.get('decodedPath'):
        frame = min(asset['decodedCount'] - 1, max(0, math.floor((shot.get('sourceStart', 0) + local) * fps + 1e-6))) + 1
        return os.path.join(asset['decodedPath'], f'frame_{frame:08d}.png')
    return asset['path']

def media_size(asset_id, shot):
    asset = assets.get(asset_id)
    if not asset or asset['kind'] not in ('image', 'video'):
        return None
    image = bpy.data.images.load(media_path(asset, 0, shot), check_existing=True)
    width, height = image.size
    return (width, height) if width > 0 and height > 0 else None

def contain(aspect, width, height):
    if not aspect:
        return width, height
    return (width, width / aspect) if aspect > width / height else (height * aspect, height)

def device_screen(device, aspect):
    a = aspect or 1.6
    if device == 'laptop': return 5.4, 3.4
    if device == 'tablet': return 5.0, 3.55
    if device == 'phone': return 2.05, 4.25
    if device == 'floating': w = min(6.2, 4.2 * a); return w, w / a
    if device == 'none': w = min(7.2, 4.8 * a); return w, w / a
    return 5.9, 3.7

def ease(t, kind='smooth'):
    t = clamp(t)
    if kind == 'linear': return t
    if kind == 'ease-out': return 1 - (1 - t) ** 3
    if kind == 'dramatic': return 16 * t ** 5 if t < .5 else 1 - (-2 * t + 2) ** 5 / 2
    return t * t * (3 - 2 * t)

def layer_motion(local, duration, index, layer, explode):
    """Port of layerMotion in web/project.ts: (x offset, z offset, scale)."""
    screen = layer.get('attach') == 'screen'
    depth = explode * .75 * (1 + (index % 4) * .18) if screen else 0
    start = min(duration * .6, layer.get('delay', 0) + index * .12)
    t = clamp((local - start) / .9)
    out = 1 - (1 - t) ** 3
    motion = layer.get('motion', 'float')
    if motion == 'pop':
        c = 1.70158
        return 0, depth * (1 + (c + 1) * (t - 1) ** 3 + c * (t - 1) ** 2), 1 + .04 * math.sin(t * math.pi)
    if motion == 'slide':
        return (1 - out) * -.9, depth * out, 1
    if motion == 'float':
        return 0, depth * out + math.sin(local / duration * math.pi) * .35 * (explode * .4 if screen else 1), 1
    return 0, depth, 1

def screen(name, width, height, position, asset_id, crop=(0, 0, 1, 1), opacity=1, parent=None):
    vertices = [vec((-width/2, -height/2, 0)), vec((width/2, -height/2, 0)), vec((width/2, height/2, 0)), vec((-width/2, height/2, 0))]
    mesh = bpy.data.meshes.new(name)
    mesh.from_pydata(vertices, [], [(0, 1, 2, 3)])
    mesh.uv_layers.new()
    x, y, w, h = crop
    for i, uv in enumerate(((x,1-y-h),(x+w,1-y-h),(x+w,1-y),(x,1-y))):
        mesh.uv_layers.active.data[i].uv = uv
    obj = bpy.data.objects.new(name, mesh)
    bpy.context.collection.objects.link(obj)
    obj.parent = parent
    obj.location = vec(position)
    material = bpy.data.materials.new(name)
    material.use_nodes = True
    nodes = material.node_tree.nodes
    nodes.clear()
    output_node = nodes.new('ShaderNodeOutputMaterial')
    emission = nodes.new('ShaderNodeEmission')
    emission.inputs['Color'].default_value = (.09, .12, .16, 1)
    emission.inputs['Strength'].default_value = 1
    material.node_tree.links.new(emission.outputs[0], output_node.inputs['Surface'])
    texture = nodes.new('ShaderNodeTexImage')
    texture.interpolation = 'Cubic'
    if asset_id in assets:
        material.node_tree.links.new(texture.outputs['Color'], emission.inputs['Color'])
        if opacity < 1 or assets[asset_id]['kind'] == 'image':
            transparent = nodes.new('ShaderNodeBsdfTransparent')
            mix = nodes.new('ShaderNodeMixShader')
            multiply = nodes.new('ShaderNodeMath'); multiply.operation = 'MULTIPLY'; multiply.inputs[1].default_value = opacity
            material.node_tree.links.new(texture.outputs['Alpha'], multiply.inputs[0])
            material.node_tree.links.new(multiply.outputs[0], mix.inputs[0])
            material.node_tree.links.new(transparent.outputs[0], mix.inputs[1])
            material.node_tree.links.new(emission.outputs[0], mix.inputs[2])
            material.node_tree.links.new(mix.outputs[0], output_node.inputs['Surface'])
        screen_bindings.append((texture, asset_id))
    obj.data.materials.append(material)
    return obj

def camera_at(keys, time, easing):
    ordered = sorted(keys, key=lambda k:k['time'])
    if time <= ordered[0]['time']: return ordered[0]
    for a,b in zip(ordered,ordered[1:]):
        if time <= b['time']:
            t=ease((time-a['time'])/((b['time']-a['time']) or 1),easing)
            return {'position':[x+(y-x)*t for x,y in zip(a['position'],b['position'])], 'target':[x+(y-x)*t for x,y in zip(a['target'],b['target'])], 'fov':a['fov']+(b['fov']-a['fov'])*t}
    return ordered[-1]

def area(name, position, energy, color, size):
    light = bpy.data.lights.new(name, 'AREA')
    light.energy = energy; light.color = rgb(color); light.shape='DISK'; light.size=size
    obj = bpy.data.objects.new(name,light); bpy.context.collection.objects.link(obj)
    obj.location=vec(position); obj.rotation_euler=(Vector((0,0,0))-obj.location).to_track_quat('-Z','Y').to_euler()

LIGHTS = {  # key, fill, rim: (position, energy, colour); world strength
    'studio': ([3,6,6],1700,'#fff2dd',[-5,2,1],950,'#9dc3ff',[0,4,-5],1700,'#ffffff',.35),
    'soft': ([3,7,7],1000,'#fff6ea',[-6,3,4],900,'#dfe8ff',[0,5,-6],700,'#ffffff',.6),
    'dramatic': ([5,8,3],2800,'#fff0dc',[-6,1,3],250,'#6c84b0',[-2,4,-6],2400,'#ffffff',.08),
    'sunset': ([-7,2.6,4],2200,'#ffa96a',[6,2,3],600,'#6d7cff',[2,4,-6],1600,'#ff7a59',.2),
    'neon': ([3,6,6],800,'#ffffff',[-6,1,2],1700,None,[4,3,-6],2400,'#ff3df2',.08),
}

def import_model(asset):
    """Normalise like the editor: longest diagonal of four units, then offset to the same spot."""
    before = set(bpy.context.scene.objects)
    bpy.ops.import_scene.gltf(filepath=asset['path'])
    new = [o for o in bpy.context.scene.objects if o not in before]
    corners = [o.matrix_world @ Vector(c) for o in new if o.type == 'MESH' for c in o.bound_box]
    if not corners:
        return
    low = Vector((min(c.x for c in corners), min(c.y for c in corners), min(c.z for c in corners)))
    high = Vector((max(c.x for c in corners), max(c.y for c in corners), max(c.z for c in corners)))
    scale = 4 / ((high - low).length or 1)
    for obj in new:
        if obj.parent is None:
            obj.scale *= scale
            obj.location = obj.location * scale + vec([-4, -1.7, -1])

def build_device(shot, aspect, root):
    """Builds the device under root and returns (screen anchor, lowest point)."""
    color, metal, rough = FINISH.get(shot.get('finish', 'silver'), FINISH['silver'])
    body = physical('Body', color, metal, rough); bezel = physical('Bezel', '#06080a', .2, .35)
    w, h = device_screen(shot['device'], aspect)
    device = shot['device']
    if device == 'laptop':
        hinge = -h/2 - .12
        lid = empty('Lid', [0, hinge, 0], root); lid.rotation_euler = (-.16, 0, 0)
        box('Lid shell', w+.32, h+.36, .08, body, [0, h/2+.16, -.02], .08, lid); box('Bezel', w+.12, h+.14, .02, bezel, [0, h/2+.16, .025], .05, lid)
        depth = h * .78
        box('Base', w+.42, .13, depth, body, [0, hinge-.07, depth/2-.1], .06, root)
        box('Keyboard', w-.3, .012, depth*.48, physical('Keys', '#0b0d10', .2, .6), [0, hinge+.002, depth*.36], .01, root)
        return empty('Screen', [0, h/2+.17, .037], lid), hinge - .14
    if device == 'tablet':
        box('Shell', w+.36, h+.36, .13, body, [0, 0, -.02], .28, root); box('Bezel', w+.12, h+.12, .02, bezel, [0, 0, .05], .18, root)
        return empty('Screen', [0, 0, .062], root), -h/2 - .2
    if device == 'phone':
        box('Shell', w+.22, h+.22, .18, body, [0, 0, 0], .2, root); box('Bezel', w+.1, h+.1, .08, bezel, [0, 0, .06], .16, root)
        return empty('Screen', [0, 0, .102], root), -h/2 - .12
    if device == 'floating':
        box('Shell', w+.1, h+.1, .05, physical('Frame', '#15171b', .4, .3), [0, 0, -.03], .05, root)
        return empty('Screen', [0, 0, 0], root), -h/2 - .05
    if device == 'none':
        return empty('Screen', [0, 0, 0], root), -h/2
    box('Shell', w+.22, h+.22, .18, body, [0, 0, 0], .08, root); box('Bezel', w+.10, h+.10, .08, bezel, [0, 0, .11], .07, root)
    box('Stand', .3, .85, .3, body, [0, -h/2-.38, -.12], .04, root); box('Foot', 2.1, .1, 1.2, body, [0, -h/2-.78, .1], .05, root)
    return empty('Screen', [0, 0, .157], root), -h/2 - .83

def build_stage(shot, bottom):
    grounded = shot['device'] in ('desktop', 'laptop')
    floor_y = bottom if grounded else bottom - 1.1
    stage = shot['stage']
    if stage == 'desk':
        box('Floor', 200, .1, 200, shot['background'], [0, floor_y-3.05, 0], .001)
        box('Desk', 16, .28, 8, physical('Desk', '#3b3431', .1, .55), [0, floor_y-.14, 0], .04)
    elif stage == 'workspace':
        box('Floor', 200, .1, 200, '#2a2523', [0, floor_y-3.15, 0], .001)
        box('Desk', 19, .3, 8.5, physical('Wood', '#6a4c37', 0, .62), [0, floor_y-.15, .6], .05)
        box('Wall', 60, 24, .1, physical('Wall', '#3b3f45', 0, .95), [0, floor_y+9, -3.85], .001)
        w = device_screen(shot['device'], 1.6)[0]
        box('Window', 6, 4.2, .02, physical('Window', '#ffe7c4', 0, 1, emission=6), [-w/2-4.5, floor_y+5.4, -3.78], .001)
        box('Tower', 1.5, 3.6, 3.2, physical('Tower', '#121316', .5, .35), [w/2+2.4, floor_y+1.8, -1.2], .08)
        box('Light strip', .04, 3.2, .04, physical('Strip', shot['accent'], 0, .5, emission=12), [w/2+1.64, floor_y+1.8, .38], .01)
        if shot['device'] != 'laptop':
            box('Keyboard', 4.6, .14, 1.55, physical('Keyboard', '#1b1d21', .4, .5), [0, floor_y+.07, 2.5], .06)
        box('Pot', 1.0, .9, 1.0, physical('Pot', '#c9b49b', 0, .8), [-w/2-3.4, floor_y+.45, -1.6], .3)
    elif stage == 'abstract':
        box('Floor', 200, .1, 200, shot['background'], [0, floor_y-.05, 0], .001)
        for i in range(3):
            bpy.ops.mesh.primitive_torus_add(major_radius=4.5+i*1.7, minor_radius=.025, location=vec([0, -.4, -3-i]), rotation=(math.pi/2+.3+i*.2, 0, .2*i))
            bpy.context.object.data.materials.append(physical('Accent ring', shot['accent'], .5, .3, emission=1.5))
    elif stage == 'void':
        box('Floor', 200, .1, 200, '#08090b', [0, floor_y-.05, 0], .001)
    elif stage != 'gradient':
        box('Floor', 200, .1, 200, shot['background'], [0, floor_y-.05, 0], .001)
        box('Backdrop', 200, 60, .1, shot['background'], [0, floor_y+30, -28], .001)

def create_scene(shot):
    global screen_bindings
    screen_bindings=[]
    bpy.ops.object.select_all(action='SELECT'); bpy.ops.object.delete(use_global=False)
    for collection in (bpy.data.meshes,bpy.data.materials,bpy.data.curves,bpy.data.cameras,bpy.data.lights,bpy.data.actions):
        for block in list(collection):
            if block.users==0: collection.remove(block)
    scene=bpy.context.scene
    scene.render.engine='CYCLES'; scene.cycles.samples=48; scene.cycles.use_denoising=True
    try:
        prefs=bpy.context.preferences.addons['cycles'].preferences
        prefs.compute_device_type='OPTIX'; prefs.get_devices()
        for device in prefs.devices: device.use=device.type!='CPU'
        if any(d.use for d in prefs.devices): scene.cycles.device='GPU'
    except Exception: scene.cycles.device='CPU'
    scene.render.resolution_x=project['width']; scene.render.resolution_y=project['height'];scene.render.resolution_percentage=100
    scene.render.fps=fps;scene.render.image_settings.file_format='PNG';scene.render.film_transparent=False
    scene.view_settings.view_transform='Standard';scene.render.use_motion_blur=True;scene.render.motion_blur_shutter=.5
    lights=LIGHTS.get(shot.get('lighting','studio'),LIGHTS['studio']);k=shot.get('lightIntensity',1)
    background=shot['background'] if shot['stage']!='gradient' else '#%02x%02x%02x'%tuple(int((int(shot['background'][i:i+2],16)*.7+int(shot['accent'][i:i+2],16)*.3)) for i in (1,3,5))
    scene.world.use_nodes=True; scene.world.node_tree.nodes['Background'].inputs['Color'].default_value=(*rgb(background),1);scene.world.node_tree.nodes['Background'].inputs['Strength'].default_value=lights[9]*k
    size=media_size(shot.get('assetId'),shot);aspect=size[0]/size[1] if size else None
    root=empty('Device transform',[0,0,0])
    anchor,bottom=build_device(shot,aspect,root)
    w,h=device_screen(shot['device'],aspect);sw,sh=contain(aspect,w,h)
    screen('App screen',sw,sh,[0,0,0],shot.get('assetId'),parent=anchor)
    build_stage(shot,bottom)
    root.location=vec(shot['devicePosition'])
    rotation=shot['deviceRotation'];root.rotation_euler=(rotation[0],-rotation[2],rotation[1])
    layers=[]
    for layer in shot['layers']:
        asset_id=layer.get('assetId',shot.get('assetId'));crop=layer['crop']
        if layer.get('attach')=='screen':
            obj=screen(layer['id'],crop[2]*sw,crop[3]*sh,[0,0,0],asset_id,crop,layer['opacity'],parent=anchor)
        else:
            layer_size=media_size(asset_id,shot) or (1920,1080)
            layer_aspect=(layer_size[0]*crop[2])/(layer_size[1]*crop[3])
            obj=screen(layer['id'],2.1,2.1/layer_aspect,layer['position'],asset_id,crop,layer['opacity'])
            r=layer['rotation'];obj.rotation_euler=(r[0],-r[2],r[1])
        layers.append((obj,layer))
    for asset in assets.values():
        if asset['kind']=='model':import_model(asset)
    accent=shot['accent']
    area('Key',lights[0],lights[1]*k,lights[2],5);area('Fill',lights[3],lights[4]*k,lights[5] or accent,5);area('Rim',lights[6],lights[7]*k,lights[8],4)
    camera_data=bpy.data.cameras.new('Studio camera');camera_data.sensor_fit='VERTICAL'
    camera=bpy.data.objects.new('Studio camera',camera_data);bpy.context.collection.objects.link(camera);scene.camera=camera;camera.rotation_mode='QUATERNION'
    camera_data.type='PERSP';camera_data.dof.use_dof=True;camera_data.dof.aperture_fstop=7/(1+3*float(shot.get('depthOfField',0)))  # matches the preview's depth of field
    return camera,layers,(sw,sh)

def shot_frames():
    """(frame index, shot index, local time) for every output frame, matching shotAt in the editor."""
    for index in range(frames):
        time=index/fps;offset=0
        for i,shot in enumerate(project['shots']):
            if time<offset+shot['duration'] or i==len(project['shots'])-1:
                yield index,i,time-offset;break
            offset+=shot['duration']

def animate(camera,layers,shot,entries,fit):
    """Keyframe every frame of the shot so Cycles can blur real camera and layer motion."""
    previous=None;sw,sh=fit;explode=shot.get('explode',0)
    for index,local in entries:
        frame=index+1;state=camera_at(shot['camera'],local,shot.get('easing','smooth'))
        camera.location=vec(state['position']);target=vec(state['target'])
        rotation=(target-camera.location).to_track_quat('-Z','Y')
        if previous is not None and previous.dot(rotation)<0:rotation.negate()
        previous=rotation;camera.rotation_quaternion=rotation
        camera.data.angle_y=math.radians(state['fov']);camera.data.dof.focus_distance=(target-camera.location).length
        camera.keyframe_insert('location',frame=frame);camera.keyframe_insert('rotation_quaternion',frame=frame)
        camera.data.keyframe_insert('lens',frame=frame);camera.data.dof.keyframe_insert('focus_distance',frame=frame)
        section=0
        for obj,layer in layers:
            on_screen=layer.get('attach')=='screen';i=section if on_screen else 0
            if on_screen:section+=1
            dx,dz,scale=layer_motion(local,shot['duration'],i,layer,explode);p=layer['position']
            if on_screen:
                cx,cy,cw,ch=layer['crop']
                obj.location=vec([(cx+cw/2-.5)*sw+p[0]+dx,(.5-cy-ch/2)*sh+p[1],.01+p[2]+dz])
            else:obj.location=vec([p[0]+dx,p[1],p[2]+dz])
            obj.scale=(layer['scale']*scale,)*3
            obj.keyframe_insert('location',frame=frame);obj.keyframe_insert('scale',frame=frame)
    for owner in [camera,camera.data]+[obj for obj,_ in layers]:
        if owner.animation_data and owner.animation_data.action:
            for curve in owner.animation_data.action.fcurves:
                for point in curve.keyframe_points:point.interpolation='LINEAR'

plan=list(shot_frames())
for shot_index,shot in enumerate(project['shots']):
    entries=[(index,local) for index,i,local in plan if i==shot_index]
    if not entries:continue
    camera,layers,fit=create_scene(shot);animate(camera,layers,shot,entries,fit)
    for index,local in entries:
        bpy.context.scene.frame_set(index+1)
        for texture,asset_id in screen_bindings:
            texture.image=bpy.data.images.load(media_path(assets[asset_id],local,shot),check_existing=True)
        bpy.context.scene.render.filepath=os.path.join(output,f'frame_{index+1:08d}.png')
        bpy.ops.render.render(write_still=True)
        for image in list(bpy.data.images):
            if image.users==0:bpy.data.images.remove(image)
        print(f'STUDIO_FRAME {index+1}/{frames}',flush=True)
