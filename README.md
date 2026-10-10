# FileDrop

Egyszerű, magyar nyelvű fájlátadás az otthoni hálózaton. Az Angular felület és az ASP.NET Core API egyetlen, korlátozott jogosultságú Docker-konténerben fut; a fájlok és a SQLite-adatbázis külön menthető könyvtárban maradnak.

## Elkészült működés

- Telefonon és laptopon használható reszponzív böngészős felület.
- Bejelentkezés után látható közös LAN-lista, illetve linkkel, QR-kóddal vagy hat karakteres kóddal elérhető rejtett megosztás.
- Lejárat: első sikeres letöltés, 15 perc, 1 óra (alapértelmezett), 24 óra, 7 nap vagy kézi visszavonás.
- Valódi fájlfeltöltés folyamatjelzővel és letöltés eredeti fájlnéven.
- Több fájl vagy teljes mappa választható. Az alapértelmezett gyűjtemény egy közös kódot kap, de a szerver az eredeti fájlokat külön tárolja. Telefonon ezek külön tölthetők le; ZIP csak külön kérésre, közvetlenül a letöltéskor készül, és nem marad a szerveren. A külön megosztás mód továbbra is fájlonkénti kódot ad.
- Tulajdonosi és családtag-fiókok biztonságosan hash-elt jelszóval.
- A fiókjelszó legalább 4 számjegy lehet; ez csak megbízható LAN-on kényelmes, Tailscale-es távoli használat előtt erősebb védelem szükséges.
- A közös fájlok listája és közvetlen letöltése belépést kér; a kódos letöltés fiók nélkül is használható.
- A közös és a csak kódos megosztások is kapnak 6 karakteres letöltési kódot; a közös fájl a belépett listában és kóddal vendégnek is elérhető.
- Egy fiókhoz több, külön megnevezett eszköz munkamenete tartozhat; minden eszköz ugyanazt a saját előzményt látja.
- A tulajdonos külön adminisztrációs nézetben látja a családtag-fiókokat és új fiókot hozhat létre. A saját aktív megosztás visszavonható; a fizikai fájl törlődik, az előzmény megmarad.
- A feltöltéshez opcionális megnevezés és megjegyzés, a közös listában pedig feltöltőnév tartozhat.
- A webalkalmazás PWA-alapjai elkészültek. Az Android Capacitor-alkalmazás a rendszer Megosztás menüjéből egy vagy több fájlt is átvesz, majd ugyanazt a csomag/külön módot kínálja.
- A kódpróbálkozás és a bejelentkezés sebességkorlátozott.
- A lejárt fájlok és munkamenetek automatikusan takarítódnak, újraindítás után is.
- Bármely belépett családtag létrehozhat rövid, nyolckarakteres feltöltési meghívót. A vendég fiók nélkül több fájlt küldhet, a saját ideiglenes fájljait javíthatja vagy törölheti, a tulajdonos pedig átvételkor normál FileDrop-megosztássá zárja le a csomagot.
- Azonos meghívót több küldő is használhat: külön HttpOnly böngésző-munkamenetet kapnak, ezért egymás fájljait nem látják és nem törölhetik.
- Feltöltéskor választható időkorlátos publikus link. A nyolckarakteres kód kizárólag az adott fájlt vagy gyűjteményt teszi elérhetővé a `drop.tolnaioli.hu` címen; más alkalmazásadatot nem nyit meg.

## Tárhelyszabályok

- Nincs külön fájlméretkorlát: egy fájl addig tölthető fel, amíg a biztonsági tartalék megmarad.
- Nincs előre lefoglalt vagy fix összesített keret.
- A program a tényleges fájlrendszer-szabadhelyet figyeli, és mindig 100 GiB tartalékot hagy.
- A felület külön mutatja a FileDrop-fájlok méretét, a még feltölthető mennyiséget és a tartalékot.
- A párhuzamos feltöltések helyét előre lefoglalja, ezért együtt sem léphetik át a biztonságos kapacitást.
- A Windows-partíciókhoz nem nyúl. A tervezett adatútvonal `/srv/filedrop/data`, ugyanazon az Ubuntu ext4 fájlrendszeren, külön partíció és előfoglalás nélkül.

## Helyi fejlesztés

Követelmény: .NET 10 SDK, Node.js 24, pnpm 11.10.0.

API:

```powershell
cd src/FileDrop.Api
dotnet run --urls http://127.0.0.1:5199
```

Felület egy másik terminálban:

```powershell
cd src/FileDrop.Web
pnpm install --frozen-lockfile
pnpm start -- --host 127.0.0.1
```

A helyi fejlesztéshez az API indítása előtt adj meg külön egyszeri beállítási kódot a környezetben. A Docker/éles környezethez mindig külön, hosszú véletlen kódot használj.

```powershell
$env:FileDrop__SetupToken='csak-a-helyi-fejleszteshez'
dotnet run --urls http://127.0.0.1:5199
```

## Helyi név és PWA

Az alkalmazás a `Host` fejléc alapján nem kötött IP-címhez, ezért a
`filedrop.home.arpa:8090` cím is használható, ha a router vagy a helyi DNS ezt
a nevet a `192.168.0.34` címre oldja fel. Az IP-cím továbbra is működik.

A manifest, service worker és Web Share Target támogatás bekerült. A legtöbb
telefonos böngésző PWA-telepítéshez HTTPS-t kér; a sima
`http://192.168.0.34:8090` vagy `http://filedrop.home.arpa:8090` cím önmagában
nem feltétlenül tekinthető telepíthető, biztonságos eredetnek. Ehhez később
Tailscale Serve vagy helyi HTTPS-proxy szükséges.

## Android alkalmazás

Az Android-projekt a `src/FileDrop.Web/android` könyvtárban található. A jelenlegi
alkalmazás a LAN-on futó `http://192.168.0.34:8090` szolgáltatást nyitja meg, ezért
otthon internet nélkül is működik, de másik hálózatról még nem érhető el. A
telefon Megosztás menüjéből kapott fájlokat közvetlenül, memóriába másolás nélkül
tölti fel; több fájl közös gyűjteményként vagy külön megosztásokként is küldhető.

### Aláírt APK-kiadás

A kiadási aláírókulcs és a jelszófájl kizárólag a fejlesztői Windows-gépen,
a felhasználói könyvtár `AndroidKeys` mappájában marad. Nem kerül Gitbe és nem
kerülhet a Dell szerverre. A `release` Gradle-feladat szándékosan hibával
leáll, ha ez a helyi aláírási beállítás hiányzik.

A szerver a `/srv/filedrop/data/releases` könyvtárból kizárólag egy APK-t ad ki:

- `filedrop-<verzió>.apk` – az aláírt telepítő;
- `latest.json` – az aktuális változat rövid leírása.

Példa a manifestre:

```json
{
  "versionCode": 3,
  "versionName": "1.0.2",
  "fileName": "filedrop-1.0.2.apk"
}
```

Ha mindkét fájl ott van, a belépés nélküli LAN-kezdőoldal felajánlja az APK
letöltését. Az Android alkalmazás a telepített `versionCode` értékét ehhez a
manifesthez hasonlítja, és csak újabb változatnál ajánlja fel a letöltést. A
telepítés mindig kézi Android-művelet marad.

## Docker

Ellenőrzés és build:

```powershell
$env:FILEDROP_SETUP_TOKEN='csak-helyi-ellenorzeshez'
docker compose config --quiet
docker compose build
```

A Compose alapértelmezetten a Dell LAN-címén publikál: `192.168.0.34:8090`. Emellett a Tailscale Serve számára fenntart egy csak helyi loopback-portot: `127.0.0.1:8091`; ez LAN-ról nem érhető el. A Compose nem nyit routerportot és nem hoz létre Cloudflare Tunnel útvonalat.

Az éles `.env` fájl nem kerül verziókezelésbe. A konténer nem rootként fut, a saját rendszerfájlrendszere csak olvasható, minden capability el van dobva, és kizárólag `/srv/filedrop/data` írható számára.

## Nyilvános feltöltési meghívó és letöltési link

A nyilvános Cloudflare Tunnel nem közvetlenül a FileDrop alkalmazáshoz csatlakozik.
A `filedrop-public-gateway` egy külön, csak a Dell loopback címére publikált Nginx
átjáró. Kizárólag az alábbiakat engedi tovább:

- a `/u/XXXX-XXXX` meghívóoldalt és annak hash-elt JavaScript/CSS fájljait;
- az adott meghívó lekérdezését;
- vendégfájl feltöltését és a feltöltő saját ideiglenes fájljának törlését;
- a `/d/XXXX-XXXX` publikus letöltőoldalt, valamint kizárólag az adott kódhoz tartozó fájl vagy gyűjtemény letöltését.

A kezdőlap, bejelentkezés, fiókok, normál megosztások és adminisztráció ezen az
átjárón mindig `404` választ adnak. A Tunnel konténer kifelé épít kapcsolatot,
ezért routerportot nem kell nyitni. A Tunnel token titok: kizárólag a Dell
`root:root`, `600` jogosultságú `.env` fájljába kerülhet.

A publikus letöltési kód 8 karakteres, 40 bit entrópiájú és IP-alapú
sebességkorlátozás védi. Publikus megosztás csak 15 perces, 1 órás, 24 órás
vagy 7 napos lejárattal hozható létre. A link lejáratakor vagy a megosztás
visszavonásakor a publikus hozzáférés is megszűnik.

A nyilvános profil helyi kapuja a `127.0.0.1:8092` címen ellenőrizhető:

```bash
sudo docker compose --profile public up -d filedrop-public-gateway
curl -fsS http://127.0.0.1:8092/public-health
curl -o /dev/null -s -w '%{http_code}\n' http://127.0.0.1:8092/
```

Az első parancsnak egészséges konténert, a másodiknak `{"status":"ok"}` választ,
a harmadiknak `404` állapotot kell adnia. A `filedrop-public-cloudflared` csak a
valódi `FILEDROP_CF_TUNNEL_TOKEN` beállítása után indítható el.

Cloudflare Free és Pro csomagban egy HTTP-kérés legfeljebb 100 MB lehet. A
meghívóoldal ezért a kijelölt fájlokat külön kérésekben küldi: több kisebb fájl
együtt lehet nagyobb 100 MB-nál, de egyetlen 100 MB feletti fájl nyilvános
feltöltéséhez később darabolt feltöltés szükséges. LAN-on és Tailscale-en ez a
Cloudflare-korlát nem érvényes.

## Biztonsági határ

A mostani LAN-változat HTTP-t használ. Ez családi, megbízható hálózaton kényelmes, de a bejelentkezési forgalom nincs titkosítva, ezért nyilvános Wi-Fi-re vagy internetre nem szabad így kitenni. Tailscale-es távoli használat előtt HTTPS-t és szűk Tailscale-hozzáférést állítunk be; publikus idegennek csak külön, lejáró letöltési/feltöltési végpont készülhet.

## Ellenőrzött állapot

- `dotnet build`: sikeres, figyelmeztetés nélkül.
- `pnpm build`: sikeres, figyelmeztetés nélkül.
- Valódi böngészős feltöltés, kód és QR: sikeres.
- Letöltött fájl SHA-256 egyezés: sikeres.
- Többeszközös belépés, saját előzmény és visszavonás: sikeres.
- Docker image build és korlátozott jogosultságú futtatás: sikeres.
- Kétfájlos gyűjtemény-feltöltés, közös kód, külön fájlos visszatöltés és menet közben előállított ZIP: sikeres.
- Android debug APK fordítása JDK 21-gyel: sikeres.
- Android release APK 1.0.2 fordítása és v2-es aláírásának ellenőrzése: sikeres.

## Következő lépések

1. A nyilvános átjáró telepítése a Dellre és a tiltott útvonalak helyi ellenőrzése.
2. A külön Cloudflare Tunnel létrehozása, a token titkos szerveroldali beállítása és a publikus hosztnév csatlakoztatása az átjáróhoz.
3. Külső hálózatról lejárat-, visszavonás-, többküldős és jogosultsági próba.
4. Biztonsági és terhelési ellenőrzés, majd végleges dokumentáció.
