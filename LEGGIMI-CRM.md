# mp4todvd 1.8.0 — collegamento al CRM

mp4todvd parla con il CRM Tastiere Digitali con **le stesse API di VHSCapture** (`/api/cattura/*`),
ma con il suo tipo di lavoro: **i DVD da masterizzare** («N. VHS da conv. in DVD» nella scheda del cliente).

## Come si collega
1. Nel CRM: Controllo PC → 🔑 della postazione → copia il token (va bene lo stesso già usato da VHSCapture su quel PC).
2. In mp4todvd: ⚙ nella banda in alto → indirizzo del CRM (`https://crm.tastieredigitali.it`), token, «Prova collegamento», Salva.

## Come lavora
- **Avvia** → se il collegamento è attivo chiede **per quale cliente**: la lista sono le schede con DVD ancora da fare.
  Se ieri si stava lavorando per un cliente, in cima c'è «▶ Continua con Mario Rossi — DVD 2/5 (Invio)».
- Mentre converte/masterizza, nella Coda del CRM compare «💿 PC · Rossi · masterizza il DVD 3º di 5».
- A fine lavoro chiede quanti DVD contare (con «Masterizza» propone il numero di copie): **Fatto, conta** · **Scarta**
  (il totale della scheda scende, il prezzo si ricalcola) · **Non contare**.
- Annullato o errore → non si conta niente.
- Finiti i DVD del cliente lo dice e passa al prossimo. **🔢 Conteggio** corregge fatti/totali nel CRM.
- Se la rete salta, gli eventi restano in coda su disco e partono da soli.

Richiede il pacchetto NuGet `System.Text.Json` (già nel csproj): GitHub Actions lo scarica da solo.
