from PIL import Image, ImageDraw
from pathlib import Path
root=Path(r'C:\Users\spnka\Desktop\ChatGPT Projects\Spankyho Optimizer')
size=512
im=Image.new('RGBA',(size,size),(0,0,0,0))
d=ImageDraw.Draw(im)
d.rounded_rectangle((28,28,size-28,size-28),radius=118,fill=(18,21,28,255),outline=(216,55,64,255),width=12)
# subtle inner frame
d.rounded_rectangle((62,62,size-62,size-62),radius=86,outline=(74,78,91,255),width=5)
# performance bars
bars=[(164,300,205,352),(236,226,277,352),(308,144,349,352)]
for i,b in enumerate(bars):
    alpha=[150,210,255][i]
    d.rounded_rectangle(b,radius=20,fill=(235,63,73,alpha))
# baseline / speed accent
d.rounded_rectangle((144,372,369,392),radius=10,fill=(235,63,73,255))
assets=root/'src'/'SpankyhoOptimizer'/'Assets'
assets.mkdir(parents=True,exist_ok=True)
im.save(assets/'AppLogo.png')
im.save(assets/'App.ico',sizes=[(16,16),(24,24),(32,32),(48,48),(64,64),(128,128),(256,256)])
im.save(root/'installer'/'App.ico',sizes=[(16,16),(24,24),(32,32),(48,48),(64,64),(128,128),(256,256)])
print('logo assets updated')