import { Link } from 'react-router-dom';

export function NotFound() {
  return (
    <div className="flex h-[calc(100vh-4rem)] flex-col items-center justify-center gap-4 text-center">
      <h1 className="text-4xl font-bold">404</h1>
      <p className="text-muted-foreground">A página que você está procurando não existe.</p>
      <Link to="/" className="text-primary hover:underline">
        Voltar para o Início
      </Link>
    </div>
  );
}
