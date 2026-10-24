"""Build notices and a CycloneDX inventory from locally restored package metadata."""
import argparse, base64, json, os, pathlib, shutil, xml.etree.ElementTree as E

parser=argparse.ArgumentParser()
parser.add_argument('--publish', required=True)
args=parser.parse_args()
root=pathlib.Path(__file__).resolve().parents[1]
publish=pathlib.Path(args.publish).resolve(); destination=publish/'third-party'; destination.mkdir(parents=True,exist_ok=True)
assets=json.loads((root/'src/ServerTray.App/obj/project.assets.json').read_text(encoding='utf-8-sig'))
packages=pathlib.Path(os.environ.get('NUGET_PACKAGES',str(pathlib.Path.home()/'.nuget/packages')))
dependencies={key: value for key,value in assets['libraries'].items() if value['type']=='package'}
runtime=json.loads((publish/'ServerTray.runtimeconfig.json').read_text())
for framework in runtime['runtimeOptions'].get('includedFrameworks',[]):
    package=framework['name'].lower()+'.runtime.win-x64'
    dependencies.setdefault(package+'/'+framework['version'],{})
mit='Permission is hereby granted'+(root/'LICENSE').read_text().split('Permission is hereby granted',1)[1]
apache=(root/'docs/licenses/Apache-2.0.txt').read_text(encoding='utf-8')
lines=['# Third-party software','', 'ServerTray is MIT-licensed. Dependencies retain their own licenses. Full notices are in `third-party/`.','', '| Package | Version | License |','|---|---|---|']
components=[]
for key,value in sorted(dependencies.items(),key=lambda x:x[0].lower()):
    name,version=key.rsplit('/',1); folder=packages/name.lower()/version
    nuspec=next(folder.glob('*.nuspec')); metadata=E.parse(nuspec).getroot().find('{*}metadata')
    def field(n):
        element=metadata.find('{*}'+n)
        return '' if element is None or element.text is None else element.text
    license_node=metadata.find('{*}license'); license_value=field('license'); kind=license_node.get('type','') if license_node is not None else ''
    identifier=license_value if kind=='expression' else 'LicenseRef-'+name
    target=destination/(name+'-'+version);target.mkdir(exist_ok=True)
    copied=[]
    for source in folder.iterdir():
        if source.is_file() and any(word in source.name.lower() for word in ('license','notice','copyright')):
            shutil.copyfile(source,target/source.name);copied.append(source.name)
    if not copied:
        if license_value=='MIT': body=field('copyright')+'\n\n'+mit
        elif license_value=='Apache-2.0': body=field('copyright')+'\n\n'+apache
        else: raise RuntimeError('No distributable license text for '+name)
        (target/'LICENSE.txt').write_text(body,encoding='utf-8')
    elif kind=='file':
        source=(folder/license_value).resolve()
        if not source.is_relative_to(folder.resolve()): raise RuntimeError('Invalid license path')
        if source.is_file() and source.name not in copied:shutil.copyfile(source,target/source.name)
    lines.append(f'| {name} | {version} | {identifier} |')
    license_entry={'name':identifier} if identifier.startswith('LicenseRef-') else {'id':identifier}
    item={'type':'library','name':name,'version':version,'purl':f'pkg:nuget/{name}@{version}','licenses':[{'license':license_entry}]}
    digest=value.get('sha512','').removeprefix('sha512-')
    if digest:
        try:item['hashes']=[{'alg':'SHA-512','content':base64.b64decode(digest).hex()}]
        except ValueError:pass
    components.append(item)
(publish/'THIRD_PARTY_NOTICES.md').write_text('\n'.join(lines)+'\n',encoding='utf-8')
(publish/'sbom.cdx.json').write_text(json.dumps({'bomFormat':'CycloneDX','specVersion':'1.6','version':1,'metadata':{'component':{'type':'application','name':'ServerTray','version':'0.1.0-preview'}},'components':components},indent=2),encoding='utf-8')
print(f'Generated notices and SBOM for {len(components)} packages.')
