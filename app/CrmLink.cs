#nullable disable
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Mp4ToDvd
{
    // ═══════════════════════════════════════════════════════════════════════════════════════════════
    // Collegamento con il CRM Tastiere Digitali — stesse API di VHSCapture (/api/cattura/*), con il
    // «tipo» di lavoro di questo programma:
    //   mp4todvd  → tipo "dvd_conv"   : i DVD da masterizzare («N. VHS da conv. in DVD» nella scheda)
    //   DvdRescue → tipo "dvd_backup" : il blocco Riversaggio / Backup quando il supporto è DVD
    // Il CRM non si collega mai al PC: è il programma che chiama il CRM con il token della postazione
    // (lo stesso di VHSCapture: Controllo PC → 🔑). Se la rete salta, gli eventi che contano (inizio,
    // fine) finiscono in una coda su disco e partono appena il CRM risponde; ogni evento ha un codice
    // univoco, il CRM non lo conta mai due volte.
    // ═══════════════════════════════════════════════════════════════════════════════════════════════

    public static class CrmInfo
    {
        public const string Tipo = "dvd_conv";          // dvd_conv | dvd_backup
        public const string App = "mp4todvd";            // mp4todvd | DvdRescue
        public const string Pezzo = "DVD";        // i pezzi, al plurale («DVD», «CD», «musicassette»)
        public const string PezzoUno = "il DVD";     // un pezzo con l'articolo («il DVD», «il CD», «la musicassetta»)
        public const string Azione = "masterizzato";      // "masterizzato" | "recuperato"
        public const string Verbo = "masterizzare";        // "masterizzare" | "recuperare"
        public const string Icona = "💿";        // 💿 | 📀
    }

    public class CrmLavoro
    {
        public int id { get; set; }
        public string cliente { get; set; } = "";
        public string stato { get; set; } = "";
        public string tipo { get; set; } = "";
        public string tipo_etichetta { get; set; } = "";
        public int nastri_fatti { get; set; }            // pezzi del NOSTRO tipo (DVD fatti)
        public int nastri_totali { get; set; }           // pezzi del NOSTRO tipo (DVD totali)
        public int prossima { get; set; } = 1;
        public int videocassette_fatte { get; set; }
        public int videocassette_totali { get; set; }
        public int dvd_fatti { get; set; }
        public int dvd_totali { get; set; }
        public int backup_fatti { get; set; }
        public int backup_totali { get; set; }
        public string dettaglio { get; set; } = "";
        public string cartella { get; set; } = "";
        public string consegna { get; set; } = "";
        public string in_registrazione_su { get; set; } = "";
        public override string ToString() => cliente;
        public int Restano => Math.Max(0, nastri_totali - nastri_fatti);
    }

    public class CrmInizio
    {
        public int cassetta_n { get; set; }
        public int nastri_fatti { get; set; }
        public int nastri_totali { get; set; }
    }

    public class CrmFine
    {
        public int nastri_fatti { get; set; }
        public int nastri_totali { get; set; }
        public bool nastri_finiti { get; set; }
        public bool tutto_finito { get; set; }
        public int restano_altri { get; set; }
        public string cliente { get; set; } = "";
        public string stato { get; set; } = "";
    }

    /// <summary>Impostazioni del collegamento: il programma ospite le salva dove vuole (dizionario o JSON).</summary>
    public class CrmImpostazioni
    {
        public bool Attivo;
        public string Url = "";
        public string Token = "";
        public int UltimoCliente;          // id della scheda su cui si stava lavorando: «▶ Continua con…»
        public bool Configurato => Attivo && !string.IsNullOrWhiteSpace(Url) && (Token ?? "").Trim().Length >= 20;
    }

    public sealed class CrmClient : IDisposable
    {
        readonly CrmImpostazioni s;
        readonly HttpClient http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        static readonly JsonSerializerOptions Json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        static readonly string CodaFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), CrmInfo.App, "crm-coda.json");

        class Evento { public string Percorso { get; set; } = ""; public string Corpo { get; set; } = ""; }
        List<Evento> coda = new List<Evento>();
        readonly object lockCoda = new object();

        public string NomePostazione { get; private set; } = "";
        public bool Raggiungibile { get; private set; }
        public string UltimoErrore { get; private set; } = "";
        public string Versione { get; set; } = "";
        public int InCoda { get { lock (lockCoda) return coda.Count; } }

        public CrmClient(CrmImpostazioni imp)
        {
            s = imp;
            try { if (File.Exists(CodaFile)) coda = JsonSerializer.Deserialize<List<Evento>>(File.ReadAllText(CodaFile)) ?? new List<Evento>(); } catch { coda = new List<Evento>(); }
        }

        public bool Configurato => s.Configurato;
        string Base => (s.Url ?? "").Trim().TrimEnd('/');

        HttpRequestMessage Req(HttpMethod m, string percorso, object corpo = null, string url = null, string token = null)
        {
            var r = new HttpRequestMessage(m, (url ?? Base) + "/api/cattura/" + percorso);
            r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", (token ?? s.Token ?? "").Trim());
            if (corpo != null) r.Content = new StringContent(corpo is string str ? str : JsonSerializer.Serialize(corpo), Encoding.UTF8, "application/json");
            return r;
        }

        async Task<T> Chiama<T>(HttpMethod m, string percorso, object corpo = null) where T : class
        {
            if (!Configurato) return null;
            try
            {
                using (var r = await http.SendAsync(Req(m, percorso, corpo)))
                {
                    string testo = await r.Content.ReadAsStringAsync();
                    if (!r.IsSuccessStatusCode)
                    {
                        Raggiungibile = r.StatusCode != System.Net.HttpStatusCode.Unauthorized && (int)r.StatusCode < 500;
                        UltimoErrore = (int)r.StatusCode == 401 ? "token non valido o postazione scollegata dal CRM" : "il CRM ha risposto " + (int)r.StatusCode;
                        return null;
                    }
                    Raggiungibile = true; UltimoErrore = "";
                    return JsonSerializer.Deserialize<T>(testo, Json);
                }
            }
            catch (Exception ex)
            {
                Raggiungibile = false;
                UltimoErrore = ex is TaskCanceledException ? "il CRM non risponde" : "CRM non raggiungibile (" + ex.Message + ")";
                return null;
            }
        }

        /// <summary>Prova indirizzo e token prima di salvarli.</summary>
        public async Task<KeyValuePair<bool, string>> Prova(string url, string token)
        {
            try
            {
                using (var r = await http.SendAsync(Req(HttpMethod.Get, "ping", null, (url ?? "").Trim().TrimEnd('/'), token)))
                {
                    string testo = await r.Content.ReadAsStringAsync();
                    if ((int)r.StatusCode == 401) return new KeyValuePair<bool, string>(false, "Il CRM ha rifiutato il token: generane uno nuovo in Controllo PC → 🔑 della postazione.");
                    if (!r.IsSuccessStatusCode) return new KeyValuePair<bool, string>(false, "Il CRM ha risposto " + (int)r.StatusCode + ".");
                    using (var d = JsonDocument.Parse(testo))
                        return new KeyValuePair<bool, string>(true, "Collegato: questo PC è «" + d.RootElement.GetProperty("postazione").GetString() + "» nel CRM.");
                }
            }
            catch (Exception ex) { return new KeyValuePair<bool, string>(false, "CRM non raggiungibile: " + ex.Message); }
        }

        class RispostaLavori { public string postazione { get; set; } = ""; public List<CrmLavoro> lavori { get; set; } = new List<CrmLavoro>(); }

        /// <summary>Clienti con pezzi del nostro tipo ancora da fare. null = CRM non raggiungibile.</summary>
        public async Task<List<CrmLavoro>> Lavori()
        {
            var r = await Chiama<RispostaLavori>(HttpMethod.Get, "lavori?tipo=" + CrmInfo.Tipo);
            if (r == null) return null;
            NomePostazione = r.postazione ?? "";
            return r.lavori ?? new List<CrmLavoro>();
        }

        public Task<CrmLavoro> Lavoro(int vhsId) => Chiama<CrmLavoro>(HttpMethod.Get, "lavoro/" + vhsId + "?tipo=" + CrmInfo.Tipo);

        class RispostaBattito { public string config_aggiornata_il { get; set; } = ""; }

        /// <summary>Battito (~20 s): tiene la postazione «collegata» e, se stiamo lavorando, la riga dal vivo nella Coda.</summary>
        public async Task Battito(bool inCorso, int vhsId, int pezzoN, int secondi)
        {
            await Chiama<RispostaBattito>(HttpMethod.Post, "stato", new
            {
                registrando = inCorso, vhs_id = inCorso ? vhsId : 0, cassetta_n = inCorso ? pezzoN : 0,
                secondi, in_pausa = false, versione = Versione, tipo = CrmInfo.Tipo,
            });
        }

        // ── eventi che contano: inizio, fine, conteggio → coda su disco se il CRM non risponde ──

        void SalvaCoda() { try { Directory.CreateDirectory(Path.GetDirectoryName(CodaFile)); File.WriteAllText(CodaFile, JsonSerializer.Serialize(coda)); } catch { } }

        static Dictionary<string, object> ConEvento(Dictionary<string, object> corpo)
        {
            if (!corpo.ContainsKey("evento_id")) corpo["evento_id"] = Guid.NewGuid().ToString("N");
            corpo["tipo"] = CrmInfo.Tipo;
            return corpo;
        }

        /// <summary>Manda subito; se il CRM non risponde mette in coda (partirà da solo). Restituisce la risposta o null.</summary>
        public async Task<string> Manda(string percorso, Dictionary<string, object> corpo)
        {
            string json = JsonSerializer.Serialize(ConEvento(corpo));
            await Svuota();
            if (InCoda == 0 && Configurato)
            {
                try
                {
                    using (var r = await http.SendAsync(Req(HttpMethod.Post, percorso, json)))
                    {
                        string testo = await r.Content.ReadAsStringAsync();
                        if (r.IsSuccessStatusCode) { Raggiungibile = true; UltimoErrore = ""; return testo; }
                        if ((int)r.StatusCode >= 400 && (int)r.StatusCode < 500 && (int)r.StatusCode != 401 && (int)r.StatusCode != 408 && (int)r.StatusCode != 429)
                        { UltimoErrore = "il CRM ha rifiutato «" + percorso + "» (" + (int)r.StatusCode + ")"; return null; }
                    }
                }
                catch (Exception ex) { Raggiungibile = false; UltimoErrore = "CRM non raggiungibile (" + ex.Message + ")"; }
            }
            lock (lockCoda) { coda.Add(new Evento { Percorso = percorso, Corpo = json }); SalvaCoda(); }
            return null;
        }

        public async Task Svuota()
        {
            if (!Configurato) return;
            while (true)
            {
                Evento e;
                lock (lockCoda) { if (coda.Count == 0) return; e = coda[0]; }
                try
                {
                    using (var r = await http.SendAsync(Req(HttpMethod.Post, e.Percorso, e.Corpo)))
                    {
                        bool scartare = (int)r.StatusCode >= 400 && (int)r.StatusCode < 500 && (int)r.StatusCode != 401 && (int)r.StatusCode != 408 && (int)r.StatusCode != 429;
                        if (!r.IsSuccessStatusCode && !scartare) { Raggiungibile = (int)r.StatusCode != 401; return; }
                        Raggiungibile = true;
                    }
                }
                catch { Raggiungibile = false; return; }
                lock (lockCoda) { if (coda.Count > 0 && ReferenceEquals(coda[0], e)) coda.RemoveAt(0); SalvaCoda(); }
            }
        }

        public static T Leggi<T>(string json) where T : class
        {
            try { return string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<T>(json, Json); } catch { return null; }
        }

        public void Dispose() => http.Dispose();
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════════
    // La sessione: tiene il cliente in corso, parla col CRM, mostra le finestre. Il programma ospite
    // chiama tre cose: PreparaCliente() prima di partire, Inizio() quando parte, Fine() quando ha finito.
    // ═══════════════════════════════════════════════════════════════════════════════════════════════
    public sealed class CrmSessione : IDisposable
    {
        public readonly CrmImpostazioni Imp;
        public readonly CrmClient Cli;
        public CrmLavoro Corrente { get; private set; }
        public bool SenzaCrm { get; private set; }         // l'operatore ha scelto «lavoro senza cliente» per questa sessione
        public bool InCorso { get; private set; }
        public int PezzoN { get; private set; }
        DateTime inizio;
        readonly System.Windows.Forms.Timer battito = new System.Windows.Forms.Timer { Interval = 20000 };
        public event Action Cambiato;                       // il programma ospite aggiorna la banda
        readonly Action<CrmImpostazioni> salva;

        public CrmSessione(CrmImpostazioni imp, Action<CrmImpostazioni> salvaImpostazioni, string versione)
        {
            Imp = imp; salva = salvaImpostazioni;
            Cli = new CrmClient(imp) { Versione = versione };
            battito.Tick += async (s, e) => { if (Cli.Configurato) { await Cli.Battito(InCorso, Corrente != null ? Corrente.id : 0, PezzoN, InCorso ? (int)(DateTime.Now - inizio).TotalSeconds : 0); await Cli.Svuota(); } };
            battito.Start();
        }

        public bool Attivo => Cli.Configurato;

        /// <summary>Testo per la banda in alto.</summary>
        public string Riga()
        {
            if (!Imp.Attivo) return "CRM non collegato · ⚙ per impostarlo";
            if (!Cli.Configurato) return "CRM: indirizzo o token mancanti · ⚙";
            if (Corrente == null) return SenzaCrm ? "Lavoro senza cliente del CRM" : "Nessun cliente scelto · scegli dal CRM";
            string r = Corrente.cliente + " · " + CrmInfo.Pezzo + " " + Corrente.nastri_fatti + "/" + Corrente.nastri_totali;
            if (InCorso) r += " · in corso il " + PezzoN + "º";
            if (!Cli.Raggiungibile && !string.IsNullOrEmpty(Cli.UltimoErrore)) r += " · ⚠ " + Cli.UltimoErrore;
            if (Cli.InCoda > 0) r += " · " + Cli.InCoda + " eventi in attesa";
            return r;
        }

        /// <summary>Prima di partire: se il CRM è attivo e non c'è un cliente, lo chiede. false = l'operatore ha annullato.</summary>
        public async Task<bool> PreparaCliente(IWin32Window owner)
        {
            if (!Cli.Configurato) return true;
            if (Corrente != null)
            {
                var agg = await Cli.Lavoro(Corrente.id);
                if (agg != null) Corrente = agg;
                if (Corrente.Restano <= 0)
                {
                    MessageBox.Show(owner, "Per " + Corrente.cliente + " " + CrmInfo.Pezzo + ": tutto fatto (" + Corrente.nastri_fatti + "/" + Corrente.nastri_totali + "). Scegli un altro cliente.", CrmInfo.App, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    Corrente = null; Imp.UltimoCliente = 0; salva(Imp); Cambiato?.Invoke();
                }
                else return true;
            }
            if (SenzaCrm) return true;
            return await ScegliCliente(owner);
        }

        /// <summary>Finestra di scelta: i clienti con pezzi da fare, «▶ Continua con…» in cima se c'era un lavoro aperto.</summary>
        public async Task<bool> ScegliCliente(IWin32Window owner)
        {
            var lavori = await Cli.Lavori();
            if (lavori == null)
            {
                var r = MessageBox.Show(owner, "Il CRM non risponde (" + Cli.UltimoErrore + ").\n\nVuoi lavorare senza cliente? Quello che fai ora non verrà contato nel CRM.", CrmInfo.App, MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (r == DialogResult.Yes) { SenzaCrm = true; Cambiato?.Invoke(); return true; }
                return false;
            }
            CrmLavoro ultimo = Imp.UltimoCliente > 0 ? lavori.FirstOrDefault(l => l.id == Imp.UltimoCliente) : null;
            using (var f = new CrmClienteForm(lavori, ultimo, Cli.NomePostazione))
            {
                if (f.ShowDialog(owner) != DialogResult.OK) return false;
                if (f.Scelto == null) { SenzaCrm = true; Corrente = null; }
                else { Corrente = f.Scelto; SenzaCrm = false; Imp.UltimoCliente = Corrente.id; salva(Imp); }
            }
            Cambiato?.Invoke();
            return true;
        }

        public void Dimentica() { Corrente = null; SenzaCrm = false; Imp.UltimoCliente = 0; salva(Imp); Cambiato?.Invoke(); }

        /// <summary>All'avvio del programma: ripropone il cliente di ieri, se ha ancora pezzi da fare.</summary>
        public async Task RiprendiUltimo()
        {
            if (!Cli.Configurato || Imp.UltimoCliente <= 0) return;
            var l = await Cli.Lavoro(Imp.UltimoCliente);
            if (l != null && l.Restano > 0 && (l.stato == "in attesa" || l.stato == "in lavorazione" || l.stato == "pronto")) Corrente = l;
            else { Imp.UltimoCliente = 0; salva(Imp); }
            Cambiato?.Invoke();
        }

        /// <summary>Parte un pezzo: il CRM assegna il numero e la Coda mostra «💿 PC2 · Rossi · masterizza il DVD 2º di 5».</summary>
        public async Task Inizio(string file)
        {
            if (Corrente == null || !Cli.Configurato) return;
            if (InCorso) await Fine("rifai", 1, "");      // il pezzo precedente non è stato chiuso: non conta
            inizio = DateTime.Now; InCorso = true; PezzoN = Corrente.prossima;
            string json = await Cli.Manda("inizio", new Dictionary<string, object> { ["vhs_id"] = Corrente.id, ["file"] = file ?? "", ["inizio"] = inizio.ToString("yyyy-MM-dd HH:mm:ss"), ["versione"] = Cli.Versione });
            var r = CrmClient.Leggi<CrmInizio>(json);
            if (r != null && r.cassetta_n > 0) PezzoN = r.cassetta_n;
            Cambiato?.Invoke();
        }

        /// <summary>Fine di un pezzo. esito: completata (conta) · scartata (non conta, il totale scende) · rifai (non conta niente).
        /// Per n pezzi fatti insieme (es. 3 copie masterizzate) chiama più volte «completata».</summary>
        public async Task<CrmFine> Fine(string esito, int quanti, string file)
        {
            if (Corrente == null || !Cli.Configurato) { InCorso = false; Cambiato?.Invoke(); return null; }
            int sec = (int)(DateTime.Now - inizio).TotalSeconds;
            CrmFine ultima = null;
            int n = esito == "completata" || esito == "scartata" ? Math.Max(1, quanti) : 1;
            for (int i = 0; i < n; i++)
            {
                string json = await Cli.Manda("fine", new Dictionary<string, object> { ["vhs_id"] = Corrente.id, ["esito"] = esito, ["cassetta_n"] = PezzoN + i, ["secondi"] = i == 0 ? sec : 0, ["file"] = file ?? "" });
                var r = CrmClient.Leggi<CrmFine>(json);
                if (r != null) ultima = r;
            }
            InCorso = false;
            if (ultima != null) { Corrente.nastri_fatti = ultima.nastri_fatti; Corrente.nastri_totali = ultima.nastri_totali; Corrente.stato = ultima.stato ?? Corrente.stato; Corrente.prossima = Math.Min(ultima.nastri_fatti + 1, Math.Max(ultima.nastri_totali, 1)); }
            else { var agg = await Cli.Lavoro(Corrente.id); if (agg != null) Corrente = agg; }
            Cambiato?.Invoke();
            return ultima;
        }

        /// <summary>A lavoro finito: chiede quanti pezzi contare, aggiorna il CRM e, se il cliente è completo, lo dice.</summary>
        public async Task ChiediFine(IWin32Window owner, int pezziFatti, string file, bool erroreOAnnullato)
        {
            if (Corrente == null || !Cli.Configurato) { InCorso = false; Cambiato?.Invoke(); return; }
            if (erroreOAnnullato) { await Fine("rifai", 1, file); return; }
            using (var f = new CrmFineForm(Corrente, pezziFatti))
            {
                f.ShowDialog(owner);
                var r = await Fine(f.Esito, f.Quanti, file);
                if (r != null && r.nastri_finiti)
                {
                    string msg = CrmInfo.Icona + " Per " + Corrente.cliente + " " + CrmInfo.Pezzo + ": tutto fatto, " + r.nastri_fatti + " di " + r.nastri_totali + ".";
                    if (r.tutto_finito) msg += "\n\nLa scheda è completa: nel CRM risulta «pronta».";
                    else if (r.restano_altri > 0) msg += "\n\nRestano altri " + r.restano_altri + " supporti da lavorare con gli altri programmi.";
                    MessageBox.Show(owner, msg, CrmInfo.App, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    Dimentica();
                }
            }
        }

        /// <summary>Correggi il conteggio del cliente (fatti / totali): il CRM ricalcola stato e prezzo.</summary>
        public async Task Riconteggio(IWin32Window owner)
        {
            if (Corrente == null || !Cli.Configurato) return;
            var agg = await Cli.Lavoro(Corrente.id); if (agg != null) Corrente = agg;
            using (var f = new CrmRiconteggioForm(Corrente))
            {
                if (f.ShowDialog(owner) != DialogResult.OK) return;
                string json = await Cli.Manda("conteggio", new Dictionary<string, object> { ["vhs_id"] = Corrente.id, ["fatti"] = f.Fatti, ["totali"] = f.Totali });
                var r = CrmClient.Leggi<CrmLavoro>(json);
                if (r != null) Corrente = r;
            }
            Cambiato?.Invoke();
        }

        public void Impostazioni(IWin32Window owner)
        {
            using (var f = new CrmSettingsForm(Imp, Cli))
            {
                if (f.ShowDialog(owner) == DialogResult.OK) { salva(Imp); Cambiato?.Invoke(); }
            }
        }

        public void Dispose() { battito.Stop(); battito.Dispose(); Cli.Dispose(); }
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════════
    // Interfaccia: banda in alto + finestre
    // ═══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>La banda in alto nella finestra principale: cliente in corso, avanzamento e tre pulsanti.</summary>
    public sealed class CrmBanda : Panel
    {
        readonly Label lbl = new Label { AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, Font = new Font("Segoe UI Semibold", 10.5f) };
        readonly Button bCliente = new Button { Text = "👤 Cliente dal CRM", AutoSize = true, FlatStyle = FlatStyle.Flat, Padding = new Padding(6, 2, 6, 2) };
        readonly Button bConteggio = new Button { Text = "🔢 Conteggio", AutoSize = true, FlatStyle = FlatStyle.Flat, Padding = new Padding(6, 2, 6, 2) };
        readonly Button bOpz = new Button { Text = "⚙", AutoSize = true, FlatStyle = FlatStyle.Flat, Padding = new Padding(6, 2, 6, 2) };
        readonly CrmSessione sess;
        readonly Form owner;

        public CrmBanda(CrmSessione sessione, Form proprietario)
        {
            sess = sessione; owner = proprietario;
            Dock = DockStyle.Top; Height = 44; Padding = new Padding(8, 6, 8, 6);
            BackColor = Color.FromArgb(240, 236, 248);
            var flow = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Padding = new Padding(0), Margin = new Padding(0) };
            flow.Controls.Add(bCliente); flow.Controls.Add(bConteggio); flow.Controls.Add(bOpz);
            lbl.Dock = DockStyle.Fill;
            Controls.Add(lbl); Controls.Add(flow);
            bCliente.Click += async (s, e) => { if (!sess.Attivo) { sess.Impostazioni(owner); return; } if (sess.InCorso) { MessageBox.Show(owner, "C'è un lavoro in corso: cambia cliente quando ha finito.", CrmInfo.App); return; } await sess.ScegliCliente(owner); };
            bConteggio.Click += async (s, e) => { if (sess.Corrente == null) { MessageBox.Show(owner, "Prima scegli il cliente dal CRM.", CrmInfo.App); return; } await sess.Riconteggio(owner); };
            bOpz.Click += (s, e) => sess.Impostazioni(owner);
            sess.Cambiato += () => { if (IsHandleCreated) BeginInvoke((Action)Aggiorna); else Aggiorna(); };
            Aggiorna();
        }

        public void Aggiorna()
        {
            lbl.Text = CrmInfo.Icona + "  " + sess.Riga();
            bool c = sess.Corrente != null;
            bConteggio.Visible = c;
            bCliente.Text = c ? "👤 Cambia cliente" : (sess.Attivo ? "👤 Cliente dal CRM" : "👤 Collega al CRM");
            BackColor = sess.InCorso ? Color.FromArgb(255, 236, 214) : (c ? Color.FromArgb(224, 244, 232) : Color.FromArgb(240, 236, 248));
        }
    }

    /// <summary>Impostazioni del collegamento: indirizzo, token della postazione, prova.</summary>
    public sealed class CrmSettingsForm : Form
    {
        public CrmSettingsForm(CrmImpostazioni imp, CrmClient cli)
        {
            Text = CrmInfo.App + " — collegamento al CRM"; FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent; ClientSize = new Size(560, 300); Font = new Font("Segoe UI", 10f);
            var chk = new CheckBox { Text = "Collegamento al CRM attivo", Checked = imp.Attivo, Location = new Point(20, 16), AutoSize = true };
            Controls.Add(chk);
            Controls.Add(new Label { Text = "Indirizzo del CRM", Location = new Point(20, 52), AutoSize = true });
            var url = new TextBox { Text = imp.Url, Location = new Point(20, 74), Width = 520 }; Controls.Add(url);
            Controls.Add(new Label { Text = "Token della postazione (Controllo PC → 🔑 della postazione; lo stesso di VHSCapture)", Location = new Point(20, 108), AutoSize = true });
            var tok = new TextBox { Text = imp.Token, Location = new Point(20, 130), Width = 520, UseSystemPasswordChar = true }; Controls.Add(tok);
            var esito = new Label { Location = new Point(20, 168), Size = new Size(520, 50), ForeColor = Color.DimGray }; Controls.Add(esito);
            var bProva = new Button { Text = "🔌 Prova collegamento", Location = new Point(20, 240), AutoSize = true }; Controls.Add(bProva);
            var bOk = new Button { Text = "💾 Salva", Location = new Point(360, 240), Width = 85, DialogResult = DialogResult.OK }; Controls.Add(bOk);
            var bNo = new Button { Text = "Annulla", Location = new Point(455, 240), Width = 85, DialogResult = DialogResult.Cancel }; Controls.Add(bNo);
            AcceptButton = bOk; CancelButton = bNo;
            bProva.Click += async (s, e) => { esito.Text = "Provo…"; var r = await cli.Prova(url.Text, tok.Text); esito.ForeColor = r.Key ? Color.DarkGreen : Color.Firebrick; esito.Text = r.Value; };
            bOk.Click += (s, e) => { imp.Attivo = chk.Checked; imp.Url = url.Text.Trim(); imp.Token = tok.Text.Trim(); };
        }
    }

    /// <summary>Scelta del cliente: lista dei lavori con pezzi da fare, «▶ Continua con…» in cima.</summary>
    public sealed class CrmClienteForm : Form
    {
        public CrmLavoro Scelto { get; private set; }
        readonly ListView lista;

        public CrmClienteForm(List<CrmLavoro> lavori, CrmLavoro ultimo, string postazione)
        {
            Text = CrmInfo.App + " — per quale cliente?"; FormBorderStyle = FormBorderStyle.Sizable; MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent; Font = new Font("Segoe UI", 10f); KeyPreview = true;
            ClientSize = new Size(Math.Min(900, Screen.PrimaryScreen.WorkingArea.Width - 60), 520); MinimumSize = new Size(640, 400);
            int y = 14;
            Controls.Add(new Label { Text = CrmInfo.Icona + " " + CrmInfo.Pezzo + " da " + CrmInfo.Verbo + " — clienti in coda" + (string.IsNullOrEmpty(postazione) ? "" : " · postazione «" + postazione + "»"), AutoSize = true, Font = new Font("Segoe UI Semibold", 14f), Location = new Point(18, y) });
            y += 36;
            Button bCont = null;
            if (ultimo != null)
            {
                bCont = new Button { Text = "▶  Continua con " + ultimo.cliente + "  —  " + CrmInfo.Pezzo + " " + ultimo.nastri_fatti + "/" + ultimo.nastri_totali + "  (Invio)", Location = new Point(18, y), Size = new Size(ClientSize.Width - 36, 44), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(224, 244, 232), Font = new Font("Segoe UI Semibold", 11f), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
                bCont.Click += (s, e) => { Scelto = ultimo; DialogResult = DialogResult.OK; };
                Controls.Add(bCont); y += 54;
            }
            lista = new ListView { View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false, Location = new Point(18, y), Size = new Size(ClientSize.Width - 36, ClientSize.Height - y - 64), Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right };
            lista.Columns.Add("Cliente", 260); lista.Columns.Add(CrmInfo.Pezzo + " fatti", 90); lista.Columns.Add("Da fare", 80); lista.Columns.Add("Stato", 110); lista.Columns.Add("Consegna", 100); lista.Columns.Add("Altri supporti", 220);
            foreach (var l in lavori)
            {
                var it = new ListViewItem(new[] { l.cliente, l.nastri_fatti + " / " + l.nastri_totali, l.Restano.ToString(), l.stato, l.consegna ?? "", l.dettaglio ?? "" }) { Tag = l };
                if (!string.IsNullOrEmpty(l.in_registrazione_su)) { it.SubItems[3].Text += " · su " + l.in_registrazione_su; it.ForeColor = Color.DimGray; }
                lista.Items.Add(it);
            }
            lista.DoubleClick += (s, e) => Conferma();
            Controls.Add(lista);
            if (lavori.Count == 0) Controls.Add(new Label { Text = "Nessun cliente ha " + CrmInfo.Pezzo + " da " + CrmInfo.Verbo + " in coda.", Location = new Point(30, y + 20), AutoSize = true, ForeColor = Color.DimGray });
            var bOk = new Button { Text = "✅ Lavora per questo cliente", AutoSize = true, Location = new Point(18, ClientSize.Height - 48), Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
            bOk.Click += (s, e) => Conferma(); Controls.Add(bOk);
            var bSenza = new Button { Text = "Senza cliente (non conta nel CRM)", AutoSize = true, Location = new Point(300, ClientSize.Height - 48), Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
            bSenza.Click += (s, e) => { Scelto = null; DialogResult = DialogResult.OK; }; Controls.Add(bSenza);
            var bNo = new Button { Text = "Annulla", AutoSize = true, Location = new Point(ClientSize.Width - 110, ClientSize.Height - 48), Anchor = AnchorStyles.Bottom | AnchorStyles.Right, DialogResult = DialogResult.Cancel }; Controls.Add(bNo);
            CancelButton = bNo;
            if (bCont != null) { AcceptButton = bCont; bCont.Select(); } else { AcceptButton = bOk; if (lista.Items.Count > 0) lista.Items[0].Selected = true; lista.Select(); }
        }

        void Conferma()
        {
            if (lista.SelectedItems.Count == 0) { MessageBox.Show(this, "Scegli un cliente dalla lista.", CrmInfo.App); return; }
            Scelto = (CrmLavoro)lista.SelectedItems[0].Tag; DialogResult = DialogResult.OK;
        }
    }

    /// <summary>Fine lavoro: quanti pezzi contare, oppure non contare / scartare.</summary>
    public sealed class CrmFineForm : Form
    {
        public string Esito { get; private set; } = "rifai";
        public int Quanti => (int)num.Value;
        readonly NumericUpDown num;

        public CrmFineForm(CrmLavoro l, int pezzi)
        {
            Text = CrmInfo.App + " — fatto"; FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent; ClientSize = new Size(620, 262); Font = new Font("Segoe UI", 10f); KeyPreview = true;
            Controls.Add(new Label { Text = CrmInfo.Icona + " " + l.cliente + " — " + CrmInfo.Pezzo + " " + l.nastri_fatti + " di " + l.nastri_totali + " già fatti", AutoSize = true, Font = new Font("Segoe UI Semibold", 13f), Location = new Point(20, 16) });
            Controls.Add(new Label { Text = "Numero di " + CrmInfo.Pezzo + " da segnare come fatti nel CRM", AutoSize = true, Location = new Point(20, 62) });
            num = new NumericUpDown { Minimum = 1, Maximum = 99, Value = Math.Max(1, Math.Min(99, pezzi)), Location = new Point(20, 86), Width = 80, Font = new Font("Segoe UI", 14f) };
            Controls.Add(num);
            int restano = Math.Max(0, l.nastri_totali - l.nastri_fatti);
            Controls.Add(new Label { Text = "Ne restano " + restano + " da " + CrmInfo.Verbo + " per questo cliente.", AutoSize = true, Location = new Point(115, 94), ForeColor = Color.DimGray });
            var b1 = new Button { Text = "✅ Fatto, conta (Invio)", Location = new Point(20, 150), Size = new Size(200, 44), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(224, 244, 232) };
            var b2 = new Button { Text = "🗑 Scarta: " + CrmInfo.PezzoUno + " non si fa (totale giù)", Location = new Point(230, 150), Size = new Size(240, 44), FlatStyle = FlatStyle.Flat };
            var b3 = new Button { Text = "🔄 Non contare", Location = new Point(480, 150), Size = new Size(120, 44), FlatStyle = FlatStyle.Flat };
            b1.Click += (s, e) => { Esito = "completata"; Close(); };
            b2.Click += (s, e) => { Esito = "scartata"; Close(); };
            b3.Click += (s, e) => { Esito = "rifai"; Close(); };
            Controls.Add(b1); Controls.Add(b2); Controls.Add(b3);
            Controls.Add(new Label { Text = "«Scarta» toglie " + CrmInfo.PezzoUno + " dal totale della scheda e il prezzo si ricalcola; «Non contare» lascia tutto com'è.", AutoSize = true, Location = new Point(20, 212), ForeColor = Color.DimGray, Font = new Font("Segoe UI", 8.5f) });
            AcceptButton = b1; b1.Select();
        }
    }

    /// <summary>Correzione del conteggio del cliente.</summary>
    public sealed class CrmRiconteggioForm : Form
    {
        public int Fatti => (int)nF.Value;
        public int Totali => (int)nT.Value;
        readonly NumericUpDown nF, nT;

        public CrmRiconteggioForm(CrmLavoro l)
        {
            Text = CrmInfo.App + " — correggi il conteggio"; FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent; ClientSize = new Size(520, 210); Font = new Font("Segoe UI", 10f);
            Controls.Add(new Label { Text = CrmInfo.Icona + " " + l.cliente, AutoSize = true, Font = new Font("Segoe UI Semibold", 13f), Location = new Point(20, 16) });
            Controls.Add(new Label { Text = "Secondo il CRM: " + l.nastri_fatti + " di " + l.nastri_totali + " " + CrmInfo.Pezzo + ". Correggi e il CRM si aggiorna subito (se cambia il totale, cambia anche il prezzo).", Location = new Point(20, 50), Size = new Size(480, 40), ForeColor = Color.DimGray });
            Controls.Add(new Label { Text = CrmInfo.Pezzo + " fatti", AutoSize = true, Location = new Point(20, 100) });
            nF = new NumericUpDown { Minimum = 0, Maximum = 999, Value = Math.Min(999, l.nastri_fatti), Location = new Point(20, 122), Width = 90, Font = new Font("Segoe UI", 13f) }; Controls.Add(nF);
            Controls.Add(new Label { Text = CrmInfo.Pezzo + " totali", AutoSize = true, Location = new Point(140, 100) });
            nT = new NumericUpDown { Minimum = 0, Maximum = 999, Value = Math.Min(999, l.nastri_totali), Location = new Point(140, 122), Width = 90, Font = new Font("Segoe UI", 13f) }; Controls.Add(nT);
            var bOk = new Button { Text = "💾 Salva nel CRM", Location = new Point(300, 124), Width = 120, DialogResult = DialogResult.OK };
            var bNo = new Button { Text = "Annulla", Location = new Point(428, 124), Width = 80, DialogResult = DialogResult.Cancel };
            bOk.Click += (s, e) => { if (nF.Value > nT.Value) { MessageBox.Show(this, "I fatti non possono superare il totale.", CrmInfo.App); DialogResult = DialogResult.None; } };
            Controls.Add(bOk); Controls.Add(bNo); AcceptButton = bOk; CancelButton = bNo;
        }
    }
}
