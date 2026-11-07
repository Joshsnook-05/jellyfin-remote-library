const assert = require('node:assert/strict');
const fs = require('node:fs');
const { JSDOM } = require('jsdom');
const js = fs.readFileSync(require('node:path').join(__dirname, '../Jellyfin.Plugin.RemoteLibrary/Web/integration.js'), 'utf8');
const dom = new JSDOM('<div id="itemDetailPage"><div class="detailPagePrimaryContent"><div class="overview">Description</div></div></div>', {url:'https://local.test/jellyfin/web/#/details?id=abc', runScripts:'outside-only'});
const w = dom.window;
w.URL = URL; w.Request = Request; w.Response = Response; w.Headers = Headers;
const sources = [
 {Id:'abc',Name:'Local file',MediaStreams:[{Type:'Video',Width:1920,Height:1080},{Type:'Audio',Language:'eng'}],Bitrate:10000000},
 {Id:'remote_friend_movie_version',Name:'Friend · 4K',MediaStreams:[{Type:'Video',Width:3840,Height:2160},{Type:'Audio',Language:'fra'}],Bitrate:30000000},
 {Id:'remote_offline_movie_version',Name:'Offline · 8K',MediaStreams:[{Type:'Video',Width:7680,Height:4320},{Type:'Audio',Language:'fra'}],Bitrate:80000000}
];
w.api = () => ({serverId: () => 'local', getCurrentUserId: () => 'alice', getUrl: () => 'https://local.test/jellyfin/', getItem: async () => ({Id:'abc', Type:'Movie',Path:'/media/movie.mkv'})});
w.jfJson = async () => ({MediaSources:sources});
w.remoteSourcesForItem = async () => [];
w.detailItemId = () => 'abc';
w.itemHasLocalFile = item => item.Path.endsWith('.mkv');
w.loadReachability = async () => [{label:'Friend',online:true},{label:'Offline',online:false}];
w.nativeFetch = async (input, init) => { w.lastRequest = {input,init}; return new Response('{}'); };
w.eval(js.slice(js.indexOf('  const playbackChoices'), js.indexOf('  function scheduleTrustPanel()')) + '\n' + js.slice(js.indexOf('  window.fetch = async function'),js.indexOf('  // Enhanced\'s API client')) + '\nwindow.mountPanel = mountTrustPanel;');
(async () => {
 await w.mountPanel();
 const box = w.document.querySelector('.remote-library-source-box');
 assert(box);
 assert.equal(box.nextElementSibling.className,'overview');
 assert.match(box.textContent,/Selected server: Local/);
 assert.match(box.textContent,/Best reported quality: Friend · 2160p/);
 const select = box.querySelector('[aria-label="Playback server"]');
 assert.equal(select.options[2].disabled,true);
 select.value = sources[1].Id;
 select.dispatchEvent(new w.Event('change'));
 assert.match(box.textContent,/Selected server: Friend/);
 const language = box.querySelector('[aria-label="Preferred audio language"]');
 language.value = 'fr'; language.dispatchEvent(new w.Event('change'));
 assert.match(box.textContent,/Preferred audio available on: Friend\./);
 assert.equal(w.localStorage.getItem('remote-library-language:local:alice'),'fr');
 await w.fetch('/jellyfin/Items/abc/PlaybackInfo', {method:'POST',body:'{"MediaSourceId":"abc","AudioStreamIndex":1}'});
 assert.equal(new URL(w.lastRequest.input).searchParams.get('MediaSourceId'),sources[1].Id);
 assert.equal(JSON.parse(w.lastRequest.init.body).MediaSourceId,sources[1].Id);
 const request = new Request('https://local.test/jellyfin/Items/abc/PlaybackInfo', {method:'POST',headers:{'Authorization':'test'},body:'{"MediaSourceId":"abc"}'});
 await w.fetch(request);
 assert.equal(w.lastRequest.input.headers.get('Authorization'),'test');
 assert.equal((await w.lastRequest.input.json()).MediaSourceId,sources[1].Id);
 await w.mountPanel();
 assert.equal(w.document.querySelectorAll('.remote-library-source-box').length,1);
 console.log('Details panel placement, local/remote switching, offline exclusion, quality, language persistence and fetch routing passed.');
 dom.window.close();
})().catch(error => { console.error(error); process.exitCode=1; dom.window.close(); });
