import type { Metadata } from "next";
import { cookies } from "next/headers";
import { Geist_Mono } from "next/font/google";
import "./globals.css";
import { Providers } from "@/components/providers";
import { Shell } from "@/components/shell";
import { ThemeProvider } from "@/components/theme-provider";
import { AssistantSessionProvider } from "@/components/assistant-drawer";

const geistMono = Geist_Mono({ subsets: ["latin"], variable: "--font-geist-mono" });

export const metadata: Metadata = { title: "Software Factory", description: "Local AI development operations" };
export default async function RootLayout({ children }: Readonly<{ children: React.ReactNode }>) {
  const cookieStore = await cookies();
  const sidebarState = cookieStore.get("sidebar_state")?.value;
  const defaultSidebarOpen = sidebarState !== "false";

  return (
    <html lang="en" suppressHydrationWarning className={geistMono.variable}>
      <body>
        <ThemeProvider attribute="class" defaultTheme="dark" enableSystem disableTransitionOnChange>
          <Providers>
            <AssistantSessionProvider>
              <Shell defaultSidebarOpen={defaultSidebarOpen}>{children}</Shell>
            </AssistantSessionProvider>
          </Providers>
        </ThemeProvider>
      </body>
    </html>
  );
}
