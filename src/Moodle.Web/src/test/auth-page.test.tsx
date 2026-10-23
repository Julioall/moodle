import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { AuthPage } from '../features/auth/AuthPage';
import { getSafeLocalReturnUrl } from '../features/auth/auth-return-url';
import { authGateway } from '../features/auth/auth-gateway';

vi.mock('../features/auth/auth-gateway', () => ({
  authGateway: {
    login: vi.fn(),
    register: vi.fn(),
    connectMoodle: vi.fn(),
  },
}));

describe('AuthPage', () => {
  afterEach(() => {
    window.history.replaceState({}, '', '/');
    vi.clearAllMocks();
  });

  it('keeps a same-site OAuth authorization URL as the post-login destination', () => {
    expect(getSafeLocalReturnUrl('/authorize?client_id=chatgpt&state=abc', 'https://novascript.com.br'))
      .toBe('/authorize?client_id=chatgpt&state=abc');
  });

  it('rejects external OAuth return destinations', () => {
    expect(getSafeLocalReturnUrl('//attacker.example/steal', 'https://novascript.com.br')).toBeNull();
    expect(getSafeLocalReturnUrl('https://attacker.example/steal', 'https://novascript.com.br')).toBeNull();
  });

  it('offers account registration and continues to the first Moodle connection', async () => {
    window.history.replaceState({}, '', '/?tab=register');
    vi.mocked(authGateway.register).mockResolvedValue({ ok: true });
    const user = userEvent.setup();

    render(<AuthPage />);

    await user.type(screen.getByLabelText('Nome'), 'Tutor Moodle Conector');
    await user.type(screen.getByLabelText('E-mail'), 'tutor@example.com');
    await user.type(screen.getByLabelText('Senha'), 'senha-segura-123');
    await user.type(screen.getByLabelText('Confirmar senha'), 'senha-segura-123');
    await user.click(screen.getByRole('button', { name: 'Criar conta' }));

    await expect(authGateway.register).toHaveBeenCalledWith('Tutor Moodle Conector', 'tutor@example.com', 'senha-segura-123');
    expect(await screen.findByRole('heading', { name: 'Conectar o primeiro Moodle' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Conectar Moodle' })).toBeInTheDocument();
  });

  it('shows text feedback while login is pending without the legacy spinner', async () => {
    let finishLogin!: (response: Awaited<ReturnType<typeof authGateway.login>>) => void;
    vi.mocked(authGateway.login).mockImplementation(() => new Promise((resolve) => { finishLogin = resolve; }));
    const user = userEvent.setup();

    render(<AuthPage />);

    await user.type(screen.getByLabelText('E-mail'), 'tutor@example.com');
    await user.type(screen.getByLabelText('Senha'), 'senha-segura-123');
    await user.click(screen.getByRole('button', { name: 'Entrar' }));

    expect(await screen.findByRole('button', { name: 'Entrando…' })).toBeDisabled();
    expect(screen.queryByTestId('spinner')).not.toBeInTheDocument();

    finishLogin({ hasMoodleConnected: false });
    expect(await screen.findByRole('button', { name: 'Conectar Moodle' })).toBeInTheDocument();
  });
});
