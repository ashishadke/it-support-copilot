import { Component, signal, inject } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ChatService, Citation } from './chat.service';

// One message in the chat transcript.
interface ChatMessage {
  role: 'user' | 'assistant';
  text: string;
  sources?: Citation[];
}

@Component({
  selector: 'app-root',
  imports: [FormsModule],          // FormsModule gives us [(ngModel)] for the input box
  templateUrl: './app.html',
  styleUrl: './app.css'
})
export class App {
  private chat = inject(ChatService);

  messages = signal<ChatMessage[]>([]);  // the whole conversation (reactive)
  loading = signal(false);               // true while waiting for the API
  input = '';                            // bound to the text box

 async send() {
  const question = this.input.trim();
  if (!question || this.loading()) return;

  // Capture recent conversation BEFORE adding the new question, so the agent
  // remembers context (e.g. what it proposed before the user says "confirm").
  const history = this.messages().slice(-6).map(m => ({ role: m.role, text: m.text }));

  this.messages.update(list => [...list, { role: 'user', text: question }]);
  this.input = '';
  this.loading.set(true);

  let started = false;
  try {
    for await (const token of this.chat.askStream(question, history)) {
      if (!started) {
        // first token arrived: stop "Thinking…" and add an empty assistant bubble to fill
        started = true;
        this.loading.set(false);
        this.messages.update(list => [...list, { role: 'assistant', text: '' }]);
      }
      // append this token to the last (assistant) message
      this.messages.update(list => {
        const copy = [...list];
        const last = copy[copy.length - 1];
        copy[copy.length - 1] = { ...last, text: last.text + token };
        return copy;
      });
    }
  } catch {
    this.messages.update(list => [...list, { role: 'assistant', text: '⚠️ Could not reach the API.' }]);
  } finally {
    this.loading.set(false);
  }
}
uploadStatus = signal('');
onFileSelected(event: Event) {
  const input = event.target as HTMLInputElement;
  const file = input.files?.[0];
  if (!file) return;

  this.uploadStatus.set(`Uploading ${file.name}…`);
  this.chat.uploadDocument(file).subscribe({
    next: (res: any) => {
      this.uploadStatus.set(`✅ Uploaded ${res.file} (${res.chunksStored} chunks)`);
      input.value = '';   // reset so the same file can be picked again
    },
    error: () => this.uploadStatus.set('❌ Upload failed')
  });
}
}