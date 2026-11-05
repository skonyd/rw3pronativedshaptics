# rw3pronativedshaptics

**Razer Wolverine V3 Pro için Native DualSense Haptics Bridge — araştırma ve prototip projesi**

> **Durum (2026-11-05):** Tasarım/araştırma aşaması. Bu GitHub deposunda henüz fiziksel Wolverine V3 Pro üzerinde çalışan, doğrulanmış bir DualSense PCM → Sensa HD köprüsü **yok**. Belgelendirilmiş olasılıkları gerçek işlev olarak sunmayın.

## Amaç
PC'de DualSense'e özel HD haptic efekt üreten oyunların **sol/sağ gerçek haptic dalga biçimlerini**, adaptif tetiklere ihtiyaç duymadan, Razer Wolverine V3 Pro'nun gövde aktüatörlerinde mümkün olan en yüksek sadakatle üretmek. Sesin basından yaratılan genel titreşim, gerçek oyun yazarlı DualSense efektleriyle karıştırılmayacak.

**Kullanıcı senaryosu:** Windows 11 PC'de hikâye/sinematik oyunlar; ergonomisi yorucu geldiği için DualSense kullanmak istemeyen, buna karşın DualSense ve DSX+ sahibi bir kullanıcı. Razer Wolverine V3 Pro mevcut; amaç başka kol satın almadan bu cihazı değerlendirmek. Rekabetçi gecikme/8000 Hz veya Xbox desteği projenin birincil hedefi değildir.

## Üç açık çalışma modu

| Mod | Kaynak | Amaç | Kanıtlanmış durum |
| --- | --- | --- | --- |
| **Normal Rumble** | XInput / game rumble | Sol/sağ klasik motor efektleri, şiddet ve karışım ayarı | Tasarım tanımlı; hedef cihaz çıkışı test edilmedi |
| **Audio-to-Haptics** | Kullanıcı seçimiyle sistem/oyun sesi | Ses analiziyle titreşim; Razer Sensa'nın hazır moduyla da çalışabilmeli | Razer'ın resmî Sensa özelliği var; bağımsız köprü uygulanmadı |
| **Native DualSense Haptics** | Oyunun sanal DualSense'e yolladığı **stereo PCM haptic audio** | Gerçek oyuna özel sol/sağ HD efektleri mümkün olan en az dönüşüm kaybıyla Wolverine'in iki gövde aktüatörüne iletmek | Araştırma/prototip hipotezi; uçtan uca doğrulanmadı |

Native mod temel başarı kriteridir. Standart rumble veya audio-reactive efektlerin native mode diye etiketlenmesi yasaktır.

## Proje hafızası / devam etmek için okuma sırası

1. [AGENTS.md](AGENTS.md) — yeni AI geliştiriciye zorunlu bağlam, iş disiplini ve teslim kriterleri.
2. [docs/PROJECT_BRIEF.md](docs/PROJECT_BRIEF.md) — kullanıcı istekleri, hedefler, kapsam, başarı ölçütleri.
3. [docs/RESEARCH_AND_REFERENCES.md](docs/RESEARCH_AND_REFERENCES.md) — kaynaklar, doğrulanmış bulgular, varsayımlar.
4. [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) — üç modun veri akışı, arayüzler ve riskli teknik noktalar.
5. [docs/VALIDATION_PLAN.md](docs/VALIDATION_PLAN.md) — cihaz, yazılım, oyun ve A/B test prosedürü.
6. [docs/ROADMAP.md](docs/ROADMAP.md) — öncelikler, blokajlar ve sonraki işler.

## İlk değerlendirmede öne çıkan dış kaynaklar

- [Razer Wolverine V3 Pro resmî sayfa](https://www.razer.com/console-controllers/razer-wolverine-v3-pro)
- [Razer Wolverine V3 Pro kullanım kılavuzu (PDF)](https://dl.razerzone.com/master-guides/WolverineV3Pro/RazerWolverineV3Pro-en.pdf)
- [iFixit: Wolverine V3 Pro voice-coil aktüatörleri](https://www.ifixit.com/Guide/Razer+Wolverine+V3+Pro+Voice-Coil+Actuators+Replacement/214388)
- [Interhaptics / WYVRN: SDK API Usage](https://doc.wyvrn.com/docs/interhaptics-sdk/haptics-unity-sdk/api-usage/)
- [PadForge](https://github.com/hifihedgehog/PadForge)
- [ApexSenseBridge](https://github.com/ReynArts/ApexSenseBridge)
- [DSX / DSX+ Steam sayfası](https://store.steampowered.com/app/1812620/DualSenseX/)

## Temel ilkeler
- Üç mod **birbirinden ayırt edilebilir**, birbirini istemsizce tetiklemez; tek aktif kaynak veya açıkça seçilmiş kontrollü karışım.
- Sol ve sağ haptic kanalları önce veri düzeyinde korunur. Cihaz çıkışı bu bağımsızlığı destekliyor mu **test edilmeden varsayılmaz**.
- Düşük seviyeli HID/firmware yazma **varsayılan dışı**; önce belgelenmiş kullanıcı-modu SDK'ları denenir.
- Kernel sürücüleri, HidHide, USB/IP, DSX+ vb. kurulumları ancak açık onay, geri alma ve anlaşılır uyarılarla yapılır.
- Cihaz sökümü ve ek motor satın alımı ilk aşama için gerekli değil; donanım eşdeğerliği yalnız aktüatör sayısından çıkarılamaz.
- Başka açık kaynak projeler kopyalanmadan önce lisans ve atıfları kontrol edilir.

**Repo:** https://github.com/skonyd/rw3pronativedshaptics
