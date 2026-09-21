import Image from "next/image";
import type {CSSProperties} from "react";
export function Art({src,alt="",...props}:{src:string;alt?:string;className?:string;style?:CSSProperties;sizes?:string;loading?:"lazy"|"eager";fetchPriority?:"high"|"low"|"auto"}){
 const hero=src.includes("hero")||src.includes("editorial/"),avatar=src.includes("avatars/")||src.startsWith("/api/profile/");
 return <Image src={src} alt={alt} width={hero?1536:640} height={hero?1024:640} sizes={avatar?"160px":hero?"(max-width: 850px) 100vw, 50vw":"(max-width: 760px) 45vw, (max-width: 1150px) 25vw, 20vw"} unoptimized={src.startsWith("/api/")} {...props}/>;
}
