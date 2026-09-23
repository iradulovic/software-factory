import type { Metadata } from "next";
import "./globals.css";
import { Providers } from "@/components/providers";
import { Shell } from "@/components/shell";
import { ThemeProvider } from "@/components/theme-provider";

export const metadata: Metadata = { title: "Software Factory", description: "Local AI development operations" };
export default function RootLayout({ children }: Readonly<{ children: React.ReactNode }>) {
  return (
    <html lang="en" suppressHydrationWarning>
      <body>
        <ThemeProvider attribute="class" defaultTheme="dark" enableSystem disableTransitionOnChange>
          <Providers>
            <Shell>{children}</Shell>
          </Providers>
        </ThemeProvider>
      </body>
    </html>
  );
}
