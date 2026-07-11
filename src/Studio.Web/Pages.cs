namespace Studio.Web;

/// <summary>Pages HTML embarquées : françaises, minimales, aucun script externe.</summary>
internal static class Pages
{
    public const string Expired = """
<!doctype html>
<html lang="fr"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>Lien expiré</title>
<style>body{font-family:system-ui,sans-serif;margin:0;padding:32px;background:#f4f6f8;color:#1a1a1a;text-align:center}
h1{font-size:1.5rem}</style></head>
<body><h1>Ce lien n'est plus valable</h1>
<p>Demandez au photographe d'afficher un nouveau code QR.</p></body></html>
""";

    public static string Upload(string token) => $$"""
<!doctype html>
<html lang="fr"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>Envoyer vos photos</title>
<style>
body{font-family:system-ui,sans-serif;margin:0;padding:24px;background:#f4f6f8;color:#1a1a1a;text-align:center}
h1{font-size:1.4rem;margin:8px 0 20px}
label.big{display:block;background:#1565c0;color:#fff;font-size:1.3rem;font-weight:600;
padding:22px 16px;border-radius:14px;cursor:pointer;margin:0 auto 16px;max-width:420px}
input[type=file]{display:none}
#status{font-size:1.1rem;min-height:2em;margin-top:12px}
#bar{height:14px;background:#dfe5ea;border-radius:7px;overflow:hidden;max-width:420px;margin:10px auto;display:none}
#fill{height:100%;width:0%;background:#2e7d32;transition:width .2s}
.ok{color:#2e7d32;font-weight:700}.err{color:#c62828;font-weight:700}
</style></head>
<body>
<h1>📷 Envoyer vos photos au magasin</h1>
<label class="big">Choisir mes photos
<input id="files" type="file" multiple accept="image/*,.heic,.heif,.dng"></label>
<div id="bar"><div id="fill"></div></div>
<div id="status">Touchez le bouton, puis sélectionnez vos photos.</div>
<script>
const input=document.getElementById('files'),status=document.getElementById('status'),
bar=document.getElementById('bar'),fill=document.getElementById('fill');
let total=0;
input.addEventListener('change',()=>{
  if(!input.files.length)return;
  const data=new FormData();
  for(const f of input.files)data.append('files',f);
  const xhr=new XMLHttpRequest();
  xhr.open('POST','/u/{{token}}');
  bar.style.display='block';
  status.textContent='Envoi de '+input.files.length+' photo(s)…';
  xhr.upload.onprogress=e=>{if(e.lengthComputable)fill.style.width=Math.round(100*e.loaded/e.total)+'%';};
  xhr.onload=()=>{
    if(xhr.status==200){
      total+=JSON.parse(xhr.responseText).saved;
      status.innerHTML='<span class="ok">✓ '+total+' photo(s) reçue(s).</span><br>Vous pouvez en envoyer d\'autres ou ranger votre téléphone.';
      fill.style.width='100%';
    }else{status.innerHTML='<span class="err">Échec de l\'envoi — réessayez.</span>';}
    input.value='';
  };
  xhr.onerror=()=>{status.innerHTML='<span class="err">Échec de l\'envoi — vérifiez le WiFi et réessayez.</span>';};
  xhr.send(data);
});
</script>
</body></html>
""";
}
