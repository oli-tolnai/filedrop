# FileDrop – út a kész rendszerig

## Cél

A FileDrop három, egymástól elkülönített használati módja legyen megbízható:

1. otthoni LAN-on böngészőből és Android alkalmazásból;
2. családtagoknak távolról, kizárólag Tailscale-en;
3. idegen vagy iskolai gépről egy előre létrehozott, egyszer használható feltöltési meghívóval.

A `filedrop.home.arpa` név egyelőre nem része a feladatnak. Routerportot nem nyitunk.

## 1. LAN-os változat lezárása

- A legfrissebb verzió telepítése a Dell szerverre, majd teljes próba laptopról és telefonról.
- Egy fájl, több külön fájl, egy ZIP-csomag és teljes mappa feltöltésének ellenőrzése.
- Kód, rövid link, QR-kód, lejárat, egyszeri letöltés, visszavonás és automatikus frissítés ellenőrzése.
- Családtag-fiókokhoz szerkesztés, jelszócsere, törlés és aktív eszközök kijelentkeztetése.
- Aláírt Android release APK készítése. Az APK kapjon verziószámot, és legyen letölthető a belépés nélkül elérhető LAN-os kezdőoldalról.
- Az alkalmazás jelezze, ha újabb APK-verzió érhető el; automatikus, észrevétlen telepítést nem végzünk.
- Napi adatbázis- és fájlmentés, visszaállítási leírás, valamint egy tényleges próba-visszaállítás.
- Feltöltési hibák, megszakadt kapcsolat és megtelt tárhely felhasználóbarát kezelése; félbemaradt ideiglenes fájlok takarítása.

**Késznek tekinthető**, ha a család minden eszközén működik, az APK újratelepíthető, és egy törölt tesztpéldány mentésből visszaállítható.

## 2. Biztonságos távoli használat Tailscale-en

- Tailscale Serve adjon HTTPS-címet a FileDrophoz; az SSH-hoz és más szolgáltatásokhoz nem nyitunk internetes portot.
- Tailscale ACL/grant csak a kijelölt családtagoknak és csak a FileDrop HTTPS-portjához adjon hozzáférést. A Docker, MQTT, adatbázis és adminfelületek ne váljanak elérhetővé számukra.
- Az Android alkalmazás először a LAN-címet próbálja, azon kívül pedig a Tailscale HTTPS-címet. A felületen látszódjon, melyik kapcsolat aktív.
- A távoli kapcsolaton külön bejelentkezést és a telefonos Megosztás menüből történő feltöltést is tesztelni kell.
- A négy számjegyes fiókjelszó távoli használat előtt erősebb jelszóra vagy második védelmi rétegre cserélendő.

**Késznek tekinthető**, ha mobilinternetről, bekapcsolt Tailscale mellett minden családi funkció működik, kikapcsolt Tailscale mellett pedig a szolgáltatás nem érhető el nyilvánosan.

## 3. Egyszer használható nyilvános feltöltési meghívó

Két azonos módon védett felhasználási eset tartozik ide:

- telefonon létrehozol egy meghívót, majd például az iskolai gépen te töltesz fel vele a Dell szerverre;
- elküldöd a meghívót valakinek, aki ezen keresztül fájlt vagy képeket küld neked anélkül, hogy FileDrop-fiókot vagy Tailscale-hozzáférést kapna.

- Meghívót csak bejelentkezett családtag hozhat létre LAN-ról vagy Tailscale-ről.
- Létrehozáskor megadható a lejárat, a maximális összméret, valamint hogy egy fájl vagy több fájl tölthető-e fel. Több fájl egyetlen beérkező csomagként tartozik a meghívóhoz.
- A meghívónak adhatsz címet, hogy később tudd, kitől és mit vársz. A feltöltő opcionálisan megadhatja a nevét és egy rövid megjegyzést, de fiókot nem hozhat létre.
- A cím rövid, de interneten nem lehet hat karakteres: legalább 10 véletlen, könnyen gépelhető karaktert használunk, például `drop.tolnaioli.hu/u/7KDM-4QPX-9R`.
- A meghívó legfeljebb 10–15 percig él, az első sikeres feltöltés után azonnal érvénytelenné válik, és kézzel is visszavonható.
- A sikeres feltöltés bekerül a meghívót létrehozó felhasználó beérkezett fájljai közé; más családtag csak akkor látja, ha a tulajdonos később közössé teszi.
- A nyilvános oldalon nincs fájllista, bejelentkezés, letöltés, adminfelület vagy általános feltöltés; csak az adott meghívó használható.
- Cloudflare Tunnel kizárólag ezt a szűk nyilvános végpontot teszi elérhetővé. A fő FileDrop felület LAN/Tailscale mögött marad.
- Szükséges sebesség- és próbálkozáskorlát, tárhelyellenőrzés, fájlnév-tisztítás, naplózás és a sikertelen feltöltések automatikus takarítása.
- A feltöltött fájl nem tekinthető automatikusan biztonságosnak: megnyitás előtt a tulajdonos látja a forrást, méretet és fájltípust; később vírusellenőrzés is hozzáadható.

**Késznek tekinthető**, ha egy meghívóval pontosan egyszer lehet feltölteni, lejárat és visszavonás után nem használható, találgatással nem érhető el, és a publikus címről a FileDrop egyetlen más funkciója sem nyitható meg.

## Javasolt megvalósítási sorrend

1. LAN-os telepítés és funkcióteszt.
2. Családtag- és eszközkezelés befejezése.
3. Aláírt APK, letöltőoldal és verziójelzés.
4. Mentés és próba-visszaállítás.
5. Tailscale HTTPS és szűk jogosultságok.
6. Egyszer használható nyilvános feltöltési meghívó.
7. Biztonsági és terhelési ellenőrzés, majd végleges dokumentáció.
