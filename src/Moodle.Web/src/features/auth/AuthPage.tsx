import { FormEvent, useState } from 'react';
import { Eye, EyeOff } from 'lucide-react';

import { Button } from '@/components/ui/button';
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card';
import { MoodleLogo } from '@/components/ui/moodle-logo';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { getSafeLocalReturnUrl } from './auth-return-url';
import { authGateway } from './auth-gateway';

type AuthMode = 'login' | 'register' | 'moodle';

export function AuthPage() {
  const initialParams = new URLSearchParams(window.location.search);
  const initialMode: AuthMode = initialParams.get('tab') === 'register' ? 'register' : 'login';
  const completionUrl = getSafeLocalReturnUrl(initialParams.get('returnUrl')) ?? '/';
  const [mode, setMode] = useState<AuthMode>(initialMode);
  const [name, setName] = useState('');
  const [email, setEmail] = useState(initialParams.get('email') ?? '');
  const [password, setPassword] = useState('');
  const [passwordConfirmation, setPasswordConfirmation] = useState('');
  const [moodleAlias, setMoodleAlias] = useState('');
  const [moodleBaseUrl, setMoodleBaseUrl] = useState('');
  const [moodleUsername, setMoodleUsername] = useState('');
  const [moodlePassword, setMoodlePassword] = useState('');
  const [showPassword, setShowPassword] = useState(false);
  const [error, setError] = useState(initialParams.get('error') ?? '');
  const [pending, setPending] = useState(false);

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    setError('');
    setPending(true);

    try {
      if (mode === 'moodle') {
        if (!moodleAlias.trim() || !moodleBaseUrl.trim() || !moodleUsername.trim() || !moodlePassword) {
          setError('Preencha todos os dados do Moodle.');
          return;
        }

        await authGateway.connectMoodle({
          moodleAlias: moodleAlias.trim(),
          moodleBaseUrl: moodleBaseUrl.trim(),
          moodleUsername: moodleUsername.trim(),
          moodlePassword,
          isDefault: true,
          canWrite: false,
        });
        // Courses in progress are included automatically; additional courses
        // can be followed later from Schools.
        window.location.assign(completionUrl);
        return;
      }

      if (!email.trim() || !password || (mode === 'register' && !name.trim())) {
        setError('Preencha todos os campos.');
        return;
      }

      if (mode === 'register') {
        if (password.length < 8) {
          setError('A senha deve ter pelo menos 8 caracteres.');
          return;
        }
        if (password !== passwordConfirmation) {
          setError('As senhas não coincidem.');
          return;
        }
        await authGateway.register(name.trim(), email.trim(), password);
        setMode('moodle');
        return;
      }

      const response = await authGateway.login(email.trim(), password);
      if (response.hasMoodleConnected === false) {
        setMode('moodle');
        return;
      }
      window.location.assign(completionUrl);
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Não foi possível concluir o acesso.');
    } finally {
      setPending(false);
    }
  };

  const isLogin = mode === 'login';
  const isRegister = mode === 'register';
  const isMoodle = mode === 'moodle';
  const passwordStrength = (() => {
    if (password.length < 8) return { label: 'Muito fraca', color: 'bg-destructive', width: 'w-1/4' };
    const criteria = [/[a-z]/.test(password), /[A-Z]/.test(password), /\d/.test(password), /[^A-Za-z0-9]/.test(password)].filter(Boolean).length;
    if (criteria <= 2) return { label: 'Fraca', color: 'bg-orange-500', width: 'w-2/4' };
    if (criteria === 3 || password.length < 12) return { label: 'Média', color: 'bg-amber-500', width: 'w-3/4' };
    return { label: 'Forte', color: 'bg-status-success', width: 'w-full' };
  })();

  return (
    <div className="flex min-h-screen items-center justify-center bg-background p-4">
      <div className="w-full max-w-md space-y-6 animate-fade-in">
        <div className="flex justify-center">
          <MoodleLogo className="mx-auto w-64" />
        </div>
        <Card className="border shadow-md">
          <CardHeader className="space-y-1 pb-4">
            <CardTitle className="text-xl">{isMoodle ? 'Conectar o primeiro Moodle' : isRegister ? 'Criar conta no Moodle Conector' : 'Entrar no Moodle Conector'}</CardTitle>
            <CardDescription>
              {isMoodle
                ? 'A conexão será usada somente pelo Moodle e começa em modo de leitura.'
                : isRegister
                  ? 'Crie sua conta para acessar o portal operacional.'
                  : 'Use o e-mail e a senha exclusivos da sua conta Moodle Conector.'}
            </CardDescription>
          </CardHeader>
          <CardContent>
            <form onSubmit={submit} className="space-y-4">
              {!isMoodle && isRegister && <div className="space-y-2"><Label htmlFor="name">Nome</Label><Input id="name" required value={name} onChange={(event) => setName(event.target.value)} autoComplete="name" /></div>}
              {!isMoodle && <>
                <div className="space-y-2"><Label htmlFor="email">E-mail</Label><Input id="email" required type="email" value={email} onChange={(event) => setEmail(event.target.value)} autoComplete="email" /></div>
                <div className="space-y-2"><Label htmlFor="password">Senha</Label><div className="relative"><Input id="password" required minLength={isRegister ? 8 : undefined} type={showPassword ? 'text' : 'password'} value={password} onChange={(event) => setPassword(event.target.value)} autoComplete={isRegister ? 'new-password' : 'current-password'} className="pr-10" /><Button type="button" variant="ghost" size="icon" aria-label={showPassword ? 'Ocultar senha' : 'Mostrar senha'} className="absolute right-0 top-0 h-full" onClick={() => setShowPassword((value) => !value)}>{showPassword ? <EyeOff className="h-4 w-4" /> : <Eye className="h-4 w-4" />}</Button></div>{isRegister && <div className="space-y-1 pt-1"><div className="h-1.5 overflow-hidden rounded-full bg-muted"><div className={`h-full rounded-full transition-all ${passwordStrength.color} ${passwordStrength.width}`} /></div><p className="text-xs text-muted-foreground">Força da senha: <span className={passwordStrength.label === 'Fraca' || passwordStrength.label === 'Muito fraca' ? 'font-medium text-destructive' : 'font-medium'}>{passwordStrength.label}</span>. Use pelo menos 8 caracteres; letras, números e símbolos deixam a senha mais segura.</p></div>}</div>
                {isRegister && <div className="space-y-2"><Label htmlFor="password-confirmation">Confirmar senha</Label><Input id="password-confirmation" required minLength={8} type="password" value={passwordConfirmation} onChange={(event) => setPasswordConfirmation(event.target.value)} autoComplete="new-password" /></div>}
              </>}
              {isMoodle && <>
                <div className="rounded-md border border-primary/20 bg-primary/[0.03] p-3 text-sm text-muted-foreground">Depois de conectar, as tools usam os escopos do token e as capabilities disponíveis no Moodle. Operações de escrita continuam seguindo confirmação e auditoria.</div>
                <div className="space-y-2"><Label htmlFor="moodle-alias">Nome da conexão</Label><Input id="moodle-alias" required value={moodleAlias} onChange={(event) => setMoodleAlias(event.target.value)} placeholder="Ex.: Campus principal" /></div>
                <div className="space-y-2"><Label htmlFor="moodle-base-url">URL base do Moodle</Label><Input id="moodle-base-url" required type="url" value={moodleBaseUrl} onChange={(event) => setMoodleBaseUrl(event.target.value)} placeholder="https://moodle.exemplo.com" /></div>
                <div className="space-y-2"><Label htmlFor="moodle-username">Usuário Moodle</Label><Input id="moodle-username" required value={moodleUsername} onChange={(event) => setMoodleUsername(event.target.value)} autoComplete="username" /></div>
                <div className="space-y-2"><Label htmlFor="moodle-password">Senha Moodle</Label><Input id="moodle-password" required type="password" value={moodlePassword} onChange={(event) => setMoodlePassword(event.target.value)} autoComplete="current-password" /></div>
              </>}
              {error && <p className="text-sm text-destructive" role="alert">{error}</p>}
              <Button type="submit" className="w-full" size="lg" disabled={pending} aria-live="polite" aria-busy={pending}>
                {pending ? isMoodle ? 'Conectando…' : isRegister ? 'Criando conta…' : 'Entrando…' : isMoodle ? 'Conectar Moodle' : isRegister ? 'Criar conta' : 'Entrar'}
              </Button>
            </form>
            {!isMoodle && <div className="mt-4 text-center text-sm text-muted-foreground"><button type="button" className="text-primary hover:underline" onClick={() => { setError(''); setMode(isLogin ? 'register' : 'login'); }}>{isLogin ? 'Ainda não tenho uma conta' : 'Já tenho uma conta'}</button></div>}
          </CardContent>
        </Card>
        <p className="text-center text-xs text-muted-foreground">Acesso protegido pelo Moodle. Suas credenciais não são compartilhadas com o Moodle.</p>
      </div>
    </div>
  );
}
