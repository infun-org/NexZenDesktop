// Mirrors only_browser NexShareCrypto.kt + WifiDirectTransport/BLE ECDH logic, with fixed inputs.
import java.nio.charset.StandardCharsets;
import java.security.*;
import java.security.spec.*;
import java.util.Base64;
import javax.crypto.*;
import javax.crypto.spec.*;

public class Vectors {
    static String b64(byte[] b){return Base64.getEncoder().encodeToString(b);}
    static String hex(byte[] b){StringBuilder s=new StringBuilder();for(byte x:b)s.append(String.format("%02x",x&0xff));return s.toString();}
    static int cmp(byte[] a, byte[] b){int n=Math.min(a.length,b.length);for(int i=0;i<n;i++){int x=a[i]&0xff,y=b[i]&0xff;if(x!=y)return x-y;}return a.length-b.length;}
    static String sas(byte[] my,byte[] peer,byte[] myN,byte[] peerN,byte[] sec)throws Exception{
        boolean mf=cmp(my,peer)<=0; MessageDigest d=MessageDigest.getInstance("SHA-256");
        d.update(mf?my:peer);d.update(mf?peer:my);d.update(mf?myN:peerN);d.update(mf?peerN:myN);d.update(sec);
        byte[] h=d.digest(); int n=((h[0]&0xff)<<16)|((h[1]&0xff)<<8)|(h[2]&0xff); return String.format("%06d",n%1000000);}
    static String sid(byte[] my,byte[] peer)throws Exception{boolean mf=cmp(my,peer)<=0;MessageDigest d=MessageDigest.getInstance("SHA-256");d.update(mf?my:peer);d.update(mf?peer:my);return hex(d.digest());}
    static byte[] wfdKey(byte[] sec,String sid)throws Exception{Mac m=Mac.getInstance("HmacSHA256");m.init(new SecretKeySpec(sec,"HmacSHA256"));m.update(("nexshare_wfd_v1:"+sid).getBytes(StandardCharsets.UTF_8));byte[] prk=m.doFinal();
        m.init(new SecretKeySpec(prk,"HmacSHA256"));m.update("info".getBytes(StandardCharsets.UTF_8));m.update((byte)1);return m.doFinal();}
    static byte[] bleLinkKey(byte[] sec,String sid)throws Exception{Mac m=Mac.getInstance("HmacSHA256");m.init(new SecretKeySpec(sec,"HmacSHA256"));m.update(("nexshare_ble_link_v1:"+sid).getBytes(StandardCharsets.UTF_8));byte[] prk=m.doFinal();
        m.init(new SecretKeySpec(prk,"HmacSHA256"));m.update("info".getBytes(StandardCharsets.UTF_8));m.update((byte)1);return m.doFinal();}
    static byte[] lanKey(String sessionId,String token)throws Exception{Mac m=Mac.getInstance("HmacSHA256");m.init(new SecretKeySpec(sessionId.getBytes(StandardCharsets.UTF_8),"HmacSHA256"));byte[] prk=m.doFinal(token.getBytes(StandardCharsets.UTF_8));
        m.init(new SecretKeySpec(prk,"HmacSHA256"));m.update("nexshare_e2e_v2".getBytes(StandardCharsets.UTF_8));m.update((byte)1);return m.doFinal();}
    static byte[] gcm(byte[] key,byte[] nonce,String aadSid,byte[] pt)throws Exception{Cipher c=Cipher.getInstance("AES/GCM/NoPadding");c.init(Cipher.ENCRYPT_MODE,new SecretKeySpec(key,"AES"),new GCMParameterSpec(128,nonce));c.updateAAD(("nexshare_e2e_v1:"+aadSid).getBytes(StandardCharsets.UTF_8));return c.doFinal(pt);}
    public static void main(String[] a)throws Exception{
        // Deterministic keypairs: SHA1PRNG seeded.
        SecureRandom rA=SecureRandom.getInstance("SHA1PRNG");rA.setSeed("nexzen-vector-A".getBytes());
        SecureRandom rB=SecureRandom.getInstance("SHA1PRNG");rB.setSeed("nexzen-vector-B".getBytes());
        KeyPairGenerator g=KeyPairGenerator.getInstance("EC");g.initialize(new ECGenParameterSpec("secp256r1"),rA);KeyPair A=g.generateKeyPair();
        g.initialize(new ECGenParameterSpec("secp256r1"),rB);KeyPair B=g.generateKeyPair();
        byte[] pubA=A.getPublic().getEncoded(), pubB=B.getPublic().getEncoded();
        // Android path: peer pub decoded via X509EncodedKeySpec, KeyAgreement ECDH
        KeyAgreement ka=KeyAgreement.getInstance("ECDH");ka.init(A.getPrivate());
        ka.doPhase(KeyFactory.getInstance("EC").generatePublic(new X509EncodedKeySpec(pubB)),true);byte[] sec=ka.generateSecret();
        KeyAgreement kb=KeyAgreement.getInstance("ECDH");kb.init(B.getPrivate());kb.doPhase(A.getPublic(),true);byte[] secB=kb.generateSecret();
        if(!java.util.Arrays.equals(sec,secB))throw new RuntimeException("secret mismatch");
        byte[] nA=new byte[16],nB=new byte[16];for(int i=0;i<16;i++){nA[i]=(byte)i;nB[i]=(byte)(0xF0+i);}
        String sasA=sas(pubA,pubB,nA,nB,sec), sasB=sas(pubB,pubA,nB,nA,sec);
        if(!sasA.equals(sasB))throw new RuntimeException("sas mismatch");
        String s=sid(pubA,pubB); byte[] wk=wfdKey(sec,s);
        String lanSession="sess-1234", token="tok-ABCDEF";
        byte[] lk=lanKey(lanSession,token);
        byte[] gn=new byte[12];for(int i=0;i<12;i++)gn[i]=(byte)(0xA0+i);
        String text="Xin chào NexShare 👋";
        byte[] ctText=gcm(wk,gn,s,text.getBytes(StandardCharsets.UTF_8));
        byte[] chunk=new byte[300];for(int i=0;i<300;i++)chunk[i]=(byte)(i*7);
        byte[] ctChunk=gcm(wk,gn,s,chunk);
        byte[] ctLan=gcm(lk,gn,lanSession,text.getBytes(StandardCharsets.UTF_8));
        byte[] bk=bleLinkKey(sec,s);
        String bleUrl="{\"url\":\"https://example.com/x\",\"title\":\"T\",\"deviceName\":\"CPH2481\"}";
        byte[] ctBle=gcm(bk,gn,s,bleUrl.getBytes(StandardCharsets.UTF_8));
        System.out.println("{");
        System.out.println(" \"privA_pkcs8\":\""+b64(A.getPrivate().getEncoded())+"\",");
        System.out.println(" \"privB_pkcs8\":\""+b64(B.getPrivate().getEncoded())+"\",");
        System.out.println(" \"pubA\":\""+b64(pubA)+"\",");
        System.out.println(" \"pubB\":\""+b64(pubB)+"\",");
        System.out.println(" \"nonceA\":\""+b64(nA)+"\",");
        System.out.println(" \"nonceB\":\""+b64(nB)+"\",");
        System.out.println(" \"secret\":\""+hex(sec)+"\",");
        System.out.println(" \"sas\":\""+sasA+"\",");
        System.out.println(" \"sessionId\":\""+s+"\",");
        System.out.println(" \"wfdKey\":\""+hex(wk)+"\",");
        System.out.println(" \"lanSessionId\":\""+lanSession+"\",");
        System.out.println(" \"lanToken\":\""+token+"\",");
        System.out.println(" \"lanKey\":\""+hex(lk)+"\",");
        System.out.println(" \"gcmNonce\":\""+b64(gn)+"\",");
        System.out.println(" \"text\":\""+text+"\",");
        System.out.println(" \"ctText\":\""+b64(ctText)+"\",");
        System.out.println(" \"chunkPlain\":\""+b64(chunk)+"\",");
        System.out.println(" \"ctChunk\":\""+b64(ctChunk)+"\",");
        System.out.println(" \"ctLanText\":\""+b64(ctLan)+"\",");
        System.out.println(" \"bleLinkKey\":\""+hex(bk)+"\",");
        System.out.println(" \"bleUrlPlain\":\""+bleUrl.replace("\"","\\\"")+"\",");
        System.out.println(" \"ctBleUrl\":\""+b64(ctBle)+"\"");
        System.out.println("}");
    }
}
